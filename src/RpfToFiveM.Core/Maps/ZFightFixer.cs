using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using RpfToFiveM.Core.Crypto;
using RpfToFiveM.Core.Extraction;

namespace RpfToFiveM.Core.Maps;

public enum ZFightMode
{
    Off,
    On,

    /// <summary>Write both an untouched output and a fixed copy.</summary>
    Both,
}

public sealed class ZFightOptions
{
    /// <summary>
    /// Texture dictionaries that exist in the base game. Null means unknown, in which case a
    /// model whose textures aren't in the output is never assumed to be untextured.
    /// </summary>
    public ISet<uint>? VanillaTextureDictionaries { get; init; }

    /// <summary>Model names from the base game (hash to name), for LOD detection and readable reports.</summary>
    public IReadOnlyDictionary<uint, string>? KnownNames { get; init; }
}

public enum ZFightAction { Removed, NotChanged, Review }

public sealed record ZFightFinding(ZFightAction Action, string Message);

public sealed class ZFightSummary
{
    public int YmapsChecked { get; init; }
    public int Removed { get; init; }
    public IReadOnlyList<ZFightFinding> Findings { get; init; } = Array.Empty<ZFightFinding>();
    public string? ReportPath { get; init; }
}

/// <summary>
/// Finds two copies of the same building in the same spot. When one is a grey
/// (LOD or untextured) model and the other is textured, the grey one is deleted;
/// identical copies lose the extra one. Ymaps are only edited when nothing can be
/// linking to their entities by index.
/// </summary>
public static partial class ZFightFixer
{
    public const string ReportFileName = "zfight-report.txt";

    // "Exact same place": real accidental duplicates sit within a millimetre; Rockstar's own
    // intentional stacks (debris, panes) are 5cm+ apart or turned, so stay well inside that.
    private const float SamePlace = 0.02f;      // metres
    private const float SameRotation = 0.9998f; // |quaternion dot|, about 1.6 degrees
    private const float SameScale = 0.01f;
    private const float SameSize = 0.15f;       // relative bounding box difference

    private enum Look { Textured, Grey, Unknown }

    private sealed record Doc(string Path, YmapFile Ymap, int Order)
    {
        public string FileName => System.IO.Path.GetFileName(Path);
    }

    private sealed record Ent(Doc Doc, YmapEntity E);

    public static ZFightSummary Run(string folder, ZFightOptions? options, Action<LogEntry>? log, CancellationToken ct,
        Action<int, int>? onProgress = null, Action? checkpoint = null)
    {
        options ??= new ZFightOptions();
        var files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        var names = options.KnownNames is null ? new Dictionary<uint, string>() : new Dictionary<uint, string>(options.KnownNames);
        var models = new Dictionary<uint, string>();
        var textureDicts = new HashSet<uint>();
        foreach (var f in files)
        {
            var baseName = System.IO.Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
            uint hash = Hash(baseName);
            names.TryAdd(hash, baseName);
            switch (System.IO.Path.GetExtension(f).ToLowerInvariant())
            {
                case ".ydr": models.TryAdd(hash, f); break;
                case ".ytd": textureDicts.Add(hash); break;
            }
        }

        var archetypes = new Dictionary<uint, ArchetypeInfo>();
        foreach (var f in files.Where(f => f.EndsWith(".ytyp", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                foreach (var a in YtypFile.Parse(File.ReadAllBytes(f))) archetypes.TryAdd(a.Name, a);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                log?.Invoke(new LogEntry(LogLevel.Warning, $"Z-fight check skipped {System.IO.Path.GetFileName(f)}: {ex.Message}"));
            }
        }

        // Same-named ymaps (e.g. base + patch copies in a dump) are versions of one map; check the newest only.
        var ymapPaths = files.Where(f => f.EndsWith(".ymap", StringComparison.OrdinalIgnoreCase))
            .GroupBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(IsPatchPath).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).First())
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        var docs = new List<Doc>();
        for (int i = 0; i < ymapPaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            checkpoint?.Invoke();
            onProgress?.Invoke(i, ymapPaths.Count);
            try
            {
                docs.Add(new Doc(ymapPaths[i], YmapFile.Parse(File.ReadAllBytes(ymapPaths[i])), docs.Count));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                log?.Invoke(new LogEntry(LogLevel.Warning, $"Z-fight check skipped {System.IO.Path.GetFileName(ymapPaths[i])}: {ex.Message}"));
            }
        }
        onProgress?.Invoke(ymapPaths.Count, ymapPaths.Count);

        var ctx = new Context(docs, names, models, textureDicts, archetypes, options.VanillaTextureDictionaries);
        var findings = new List<ZFightFinding>();
        var plan = new Dictionary<Doc, Dictionary<int, string>>(); // doc -> entity index -> finding text

        foreach (var (a, b) in FindCoincidentPairs(docs, ct))
        {
            // Overlapping LOD levels are how the LOD system works; linked pairs are intended.
            if ((a.E.IsLodLevel && b.E.IsLodLevel) || ctx.IsLinked(a, b)) continue;

            var (loser, keeper, why) = ctx.Decide(a, b);
            if (why is null) continue;
            if (loser is null)
            {
                string where = $"{ctx.Name(a.E.Archetype)} ({a.Doc.FileName}) and {ctx.Name(b.E.Archetype)} ({b.Doc.FileName}) at {Fmt(a.E.Position)}";
                findings.Add(new ZFightFinding(ZFightAction.Review, $"{where}: {why}"));
                continue;
            }
            if (plan.TryGetValue(keeper!.Doc, out var keeperPlan) && keeperPlan.ContainsKey(keeper.E.Index))
                continue; // its partner is already being removed; keep this one

            if (!plan.TryGetValue(loser.Doc, out var list)) plan[loser.Doc] = list = new Dictionary<int, string>();
            list.TryAdd(loser.E.Index, $"{ctx.Name(loser.E.Archetype)} in {loser.Doc.FileName} at {Fmt(loser.E.Position)} - {why}");
        }

        int removed = Apply(plan, ctx, findings);
        var report = WriteReport(folder, docs.Count, findings);

        log?.Invoke(new LogEntry(removed > 0 || findings.Count > 0 ? LogLevel.Warning : LogLevel.Info,
            findings.Count == 0
                ? $"Z-fight check: {docs.Count:N0} ymap(s) checked, nothing found."
                : $"Z-fight check: removed {removed:N0} overlapping object(s); {findings.Count(f => f.Action != ZFightAction.Removed):N0} left for review. See {ReportFileName}."));

        return new ZFightSummary { YmapsChecked = docs.Count, Removed = removed, Findings = findings, ReportPath = report };
    }

    private static int Apply(Dictionary<Doc, Dictionary<int, string>> plan, Context ctx, List<ZFightFinding> findings)
    {
        int removed = 0;
        foreach (var (doc, entries) in plan.OrderBy(p => p.Key.Order))
        {
            bool isParentOfOthers = ctx.IsParentOfLoadedYmap(doc);
            bool hasLodLinks = doc.Ymap.Entities.Any(e => e.NumChildren > 0);
            bool emptiesFile = entries.Count == doc.Ymap.Entities.Count;

            if (!isParentOfOthers && (emptiesFile || !hasLodLinks))
            {
                if (emptiesFile)
                {
                    File.Delete(doc.Path);
                }
                else
                {
                    doc.Ymap.RemoveEntities(entries.Keys);
                    File.WriteAllBytes(doc.Path, doc.Ymap.ToRsc7());
                }
                removed += entries.Count;
                foreach (var text in entries.Values)
                    findings.Add(new ZFightFinding(ZFightAction.Removed, text + (emptiesFile ? " (ymap deleted, nothing else in it)" : "")));
            }
            else
            {
                foreach (var text in entries.Values)
                    findings.Add(new ZFightFinding(ZFightAction.NotChanged,
                        text + " — not changed: this ymap has LOD links that other maps may rely on, remove it by hand"));
            }
        }
        return removed;
    }

    private static IEnumerable<(Ent A, Ent B)> FindCoincidentPairs(List<Doc> docs, CancellationToken ct)
    {
        var grid = new Dictionary<(int, int, int), List<Ent>>();
        foreach (var doc in docs)
        {
            foreach (var e in doc.Ymap.Entities)
            {
                var key = Cell(e.Position);
                if (!grid.TryGetValue(key, out var cell)) grid[key] = cell = new List<Ent>();
                cell.Add(new Ent(doc, e));
            }
        }

        foreach (var doc in docs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var e in doc.Ymap.Entities)
            {
                var (cx, cy, cz) = Cell(e.Position);
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var cell)) continue;
                    foreach (var other in cell)
                    {
                        // Each unordered pair once: compare only against later entities.
                        if (other.Doc.Order < doc.Order || (other.Doc == doc && other.E.Index <= e.Index)) continue;
                        // Scripted ymaps are alternative states (burnt/unburnt, on/off) that never load together.
                        if (other.Doc != doc && (doc.Ymap.IsScripted || other.Doc.Ymap.IsScripted)) continue;
                        if (Vector3.Distance(e.Position, other.E.Position) > SamePlace) continue;
                        if (MathF.Abs(Quaternion.Dot(e.Rotation, other.E.Rotation)) < SameRotation) continue;
                        if (MathF.Abs(e.ScaleXY - other.E.ScaleXY) > SameScale || MathF.Abs(e.ScaleZ - other.E.ScaleZ) > SameScale) continue;
                        yield return (new Ent(doc, e), other);
                    }
                }
            }
        }
    }

    private sealed class Context
    {
        private readonly Dictionary<uint, Doc> _byName = new();
        private readonly HashSet<uint> _parentNames;
        private readonly Dictionary<uint, string> _names;
        private readonly Dictionary<uint, string> _models;
        private readonly HashSet<uint> _textureDicts;
        private readonly Dictionary<uint, ArchetypeInfo> _archetypes;
        private readonly ISet<uint>? _vanilla;
        private readonly Dictionary<uint, bool?> _embeddedCache = new();

        public Context(List<Doc> docs, Dictionary<uint, string> names, Dictionary<uint, string> models,
            HashSet<uint> textureDicts, Dictionary<uint, ArchetypeInfo> archetypes, ISet<uint>? vanilla)
        {
            foreach (var d in docs) _byName.TryAdd(d.Ymap.NameHash, d);
            _parentNames = docs.Where(d => d.Ymap.ParentHash != 0).Select(d => d.Ymap.ParentHash).ToHashSet();
            _names = names;
            _models = models;
            _textureDicts = textureDicts;
            _archetypes = archetypes;
            _vanilla = vanilla;
        }

        public string Name(uint hash) => _names.TryGetValue(hash, out var n) ? n : $"0x{hash:X8}";

        public bool IsParentOfLoadedYmap(Doc doc) => _parentNames.Contains(doc.Ymap.NameHash);

        /// <summary>True when one entity is an ancestor of the other in the LOD chain (an intended overlap).</summary>
        public bool IsLinked(Ent a, Ent b) => IsAncestor(a, b) || IsAncestor(b, a);

        private bool IsAncestor(Ent child, Ent candidate)
        {
            var current = child;
            for (int depth = 0; depth < 8; depth++) // HD -> LOD -> SLOD1..4 at most
            {
                var doc = ParentDoc(current);
                int index = current.E.ParentIndex;
                if (doc is null || index >= doc.Ymap.Entities.Count) return false;
                if (doc == candidate.Doc && index == candidate.E.Index) return true;
                current = new Ent(doc, doc.Ymap.Entities[index]);
            }
            return false;
        }

        private Doc? ParentDoc(Ent x)
        {
            var e = x.E;
            if (e.ParentIndex < 0) return null;
            var own = x.Doc.Ymap.Entities;
            bool internalLink = !e.LodInParentYmap && e.ParentIndex < own.Count && own[e.ParentIndex].LodLevel > e.LodLevel
                                && own[e.ParentIndex].LodLevel != LodLevel.OrphanHd;
            if (internalLink) return x.Doc;
            return _byName.TryGetValue(x.Doc.Ymap.ParentHash, out var p) ? p : null;
        }

        /// <summary>
        /// Picks which copy to delete. A null <c>Why</c> means "not a problem"; a null loser with
        /// a reason means "suspicious but not safe to decide automatically".
        /// </summary>
        public (Ent? Loser, Ent? Keeper, string? Why) Decide(Ent a, Ent b)
        {
            if (a.E.Archetype == b.E.Archetype)
                return (b, a, "exact duplicate of the same model in the same spot");

            // Two different models are the same building if they're a model and its LOD
            // version (x / x_lod) or have matching bounds.
            bool nameMatch = _names.TryGetValue(a.E.Archetype, out var na) && _names.TryGetValue(b.E.Archetype, out var nb)
                             && StripLod(na) == StripLod(nb);
            bool similar = _archetypes.TryGetValue(a.E.Archetype, out var aa) && _archetypes.TryGetValue(b.E.Archetype, out var ab)
                           && SimilarSize(aa.Size, ab.Size);
            if (!nameMatch && !similar) return (null, null, null);

            // Overlapping versions are normal (LOD pairs, decal overlays) unless one is a grey box.
            var (lookA, whyA) = Classify(a);
            var (lookB, whyB) = Classify(b);

            if (lookA == Look.Grey && lookB == Look.Textured) return (a, b, $"untextured copy ({whyA}) of textured {Name(b.E.Archetype)}");
            if (lookB == Look.Grey && lookA == Look.Textured) return (b, a, $"untextured copy ({whyB}) of textured {Name(a.E.Archetype)}");

            return (lookA, lookB) switch
            {
                (Look.Grey, Look.Grey) => (null, null, "two versions of the same building overlap; both look untextured"),
                (Look.Grey, Look.Unknown) or (Look.Unknown, Look.Grey) =>
                    (null, null, "two versions of the same building overlap; one looks untextured but the other couldn't be checked"),
                _ => (null, null, null),
            };
        }

        /// <summary>
        /// Grey means provably untextured: the model is in the output, carries no textures of
        /// its own, and its texture dictionary exists neither in the output nor the base game.
        /// </summary>
        private (Look Look, string Why) Classify(Ent x)
        {
            bool? embedded = Embedded(x.E.Archetype);
            if (embedded == true) return (Look.Textured, "");

            if (_archetypes.TryGetValue(x.E.Archetype, out var arch))
            {
                uint txd = arch.TextureDictionary;
                if (txd != 0 && (_textureDicts.Contains(txd) || _vanilla?.Contains(txd) == true)) return (Look.Textured, "");
                if (embedded == false && (txd == 0 || _vanilla is not null))
                    return (Look.Grey, txd == 0 ? "no textures" : $"texture dictionary {Name(txd)} not found");
            }
            return (Look.Unknown, "");
        }

        private bool? Embedded(uint archetype)
        {
            if (_embeddedCache.TryGetValue(archetype, out var cached)) return cached;
            bool? result = null;
            uint model = _archetypes.TryGetValue(archetype, out var a) && a.AssetName != 0 ? a.AssetName : archetype;
            if (_models.TryGetValue(model, out var path))
            {
                try { result = DrawableInfo.HasEmbeddedTextures(File.ReadAllBytes(path)); }
                catch (IOException) { }
            }
            return _embeddedCache[archetype] = result;
        }
    }

    private static string StripLod(string name) => LodName().Replace(name, "");

    [GeneratedRegex(@"(_s?lod\d*$|^s?lod\d*_|_s?lod\d*_)", RegexOptions.IgnoreCase)]
    private static partial Regex LodName();

    private static bool SimilarSize(Vector3 a, Vector3 b)
    {
        static bool Close(float x, float y)
        {
            float m = MathF.Max(MathF.Abs(x), MathF.Abs(y));
            return m < 0.01f || MathF.Abs(x - y) / m <= SameSize;
        }
        return Close(a.X, b.X) && Close(a.Y, b.Y) && Close(a.Z, b.Z);
    }

    private static (int, int, int) Cell(Vector3 p) => ((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z));

    private static bool IsPatchPath(string p) =>
        p.Contains("patch", StringComparison.OrdinalIgnoreCase) || p.Contains("update", StringComparison.OrdinalIgnoreCase);

    private static uint Hash(string s) => JenkHash.Hash(Encoding.ASCII.GetBytes(s.ToLowerInvariant()));

    private static string Fmt(Vector3 v) => $"({v.X:0.00}, {v.Y:0.00}, {v.Z:0.00})";

    private static string WriteReport(string folder, int ymaps, List<ZFightFinding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Z-fighting check");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}. {ymaps:N0} ymap(s) checked.");
        sb.AppendLine();
        if (findings.Count == 0) sb.AppendLine("Nothing found: no two copies of the same building share a spot.");

        foreach (var group in findings.GroupBy(f => f.Action).OrderBy(g => g.Key))
        {
            sb.AppendLine(group.Key switch
            {
                ZFightAction.Removed => $"REMOVED ({group.Count()})",
                ZFightAction.NotChanged => $"FOUND BUT NOT CHANGED ({group.Count()})",
                _ => $"FOR REVIEW ({group.Count()})",
            });
            foreach (var f in group) sb.AppendLine("  " + f.Message);
            sb.AppendLine();
        }

        var path = System.IO.Path.Combine(folder, ReportFileName);
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
