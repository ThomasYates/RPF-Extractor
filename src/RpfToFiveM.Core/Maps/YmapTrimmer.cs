using System.Text;
using RpfToFiveM.Core.Extraction;

namespace RpfToFiveM.Core.Maps;

public sealed class TrimResult
{
    public List<string> RemovedEmpty { get; } = new();
    public List<string> RemovedGrass { get; } = new();
    public int MapYmapsBefore { get; set; }
    public int ProjectedBefore { get; set; }
    public int ProjectedAfter { get; set; }
    public int Limit { get; set; }
    public int Spare { get; set; }

    /// <summary>Still over the limit after removing everything that could safely go.</summary>
    public bool StillOver => ProjectedAfter > Limit;
}

/// <summary>
/// Keeps a FiveM resource under the game's ymap limit (MapDataStore, which servers can't
/// raise). Ymaps that hold nothing are always set aside; if the map is still over the
/// limit, grass-only ymaps are set aside smallest first until it fits. Buildings are never
/// touched, and nothing is deleted: removed files go to _not_used/ymaps_removed.
/// </summary>
public static class YmapTrimmer
{
    public const string RemovedFolder = "ymaps_removed";

    private sealed record Candidate(string Path, YmapFile Ymap, long Size);

    /// <param name="baseGameYmaps">Ymaps the base game already registers.</param>
    /// <param name="limit">MapDataStore size.</param>
    /// <param name="spare">Room to leave for other resources' ymaps.</param>
    /// <param name="isBaseGameYmap">True for names the base game already has; those replace a slot rather than adding one.</param>
    public static TrimResult Trim(string resourceRoot, int baseGameYmaps, int limit, int spare, Action<LogEntry>? log,
        Func<string, bool>? isBaseGameYmap = null)
    {
        var result = new TrimResult { Limit = limit, Spare = spare };
        var stream = Path.Combine(resourceRoot, "stream");
        var files = Directory.Exists(stream)
            ? Directory.EnumerateFiles(stream, "*.ymap", SearchOption.AllDirectories).ToList()
            : new List<string>();

        var parsed = new List<Candidate>();
        foreach (var f in files)
        {
            try { parsed.Add(new Candidate(f, YmapFile.Parse(File.ReadAllBytes(f)), new FileInfo(f).Length)); }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // Unreadable ymaps are kept as they are.
            }
        }

        // A ymap other ymaps name as their LOD parent is kept even if empty, so their links stay valid.
        var parents = parsed.Where(c => c.Ymap.ParentHash != 0).Select(c => c.Ymap.ParentHash).ToHashSet();
        bool Removable(Candidate c) => !parents.Contains(c.Ymap.NameHash) && !c.Ymap.IsScripted;

        bool NewSlot(string path) => isBaseGameYmap?.Invoke(Path.GetFileNameWithoutExtension(path)) != true;
        result.MapYmapsBefore = files.Count;
        result.ProjectedBefore = baseGameYmaps + files.Count(NewSlot);
        int projected = result.ProjectedBefore;

        foreach (var c in parsed.Where(c => c.Ymap.IsEmpty && Removable(c)))
        {
            SetAside(resourceRoot, c.Path);
            result.RemovedEmpty.Add(c.Path);
            if (NewSlot(c.Path)) projected--;
        }

        int target = limit - spare;
        foreach (var c in parsed.Where(c => c.Ymap.IsGrassOnly && Removable(c)).OrderBy(c => c.Ymap.GrassBatches).ThenBy(c => c.Size).ThenBy(c => c.Path))
        {
            if (projected <= target) break;
            SetAside(resourceRoot, c.Path);
            result.RemovedGrass.Add(c.Path);
            if (NewSlot(c.Path)) projected--;
        }

        result.ProjectedAfter = projected;

        if (result.RemovedEmpty.Count > 0)
            log?.Invoke(new LogEntry(LogLevel.Info, $"Set aside {result.RemovedEmpty.Count:N0} empty ymap(s) (they held nothing)."));
        if (result.RemovedGrass.Count > 0)
            log?.Invoke(new LogEntry(LogLevel.Warning, $"Set aside {result.RemovedGrass.Count:N0} grass-only ymap(s) to fit FiveM's {limit:N0} ymap limit; " +
                "those areas lose some grass and ground plants. They're in _not_used/ymaps_removed if you want them back."));
        if (result.StillOver)
            log?.Invoke(new LogEntry(LogLevel.Error, $"Still about {projected - limit:N0} ymap(s) over FiveM's {limit:N0} limit after removing empty and grass ymaps. " +
                "The game will likely crash with \"Pool Full, Size == 12000\"; the map's ymaps need merging (e.g. in CodeWalker)."));
        return result;
    }

    public static string Describe(TrimResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Ymap limit (MapDataStore): base game + map was {r.ProjectedBefore:N0}, now {r.ProjectedAfter:N0}, " +
                      $"limit {r.Limit:N0} (aiming for {r.Limit - r.Spare:N0} to leave room for other resources).");
        if (r.RemovedEmpty.Count > 0)
        {
            sb.AppendLine($"Set aside {r.RemovedEmpty.Count:N0} empty ymap(s) (no visible change):");
            foreach (var p in r.RemovedEmpty) sb.AppendLine("  " + Path.GetFileName(p));
        }
        if (r.RemovedGrass.Count > 0)
        {
            sb.AppendLine($"Set aside {r.RemovedGrass.Count:N0} grass-only ymap(s), smallest first (those areas lose some grass):");
            foreach (var p in r.RemovedGrass) sb.AppendLine("  " + Path.GetFileName(p));
        }
        if (r.RemovedEmpty.Count + r.RemovedGrass.Count > 0)
            sb.AppendLine($"All of them are in _not_used/{RemovedFolder}; move any back into stream/ to restore it.");
        return sb.ToString();
    }

    private static void SetAside(string resourceRoot, string path)
    {
        var rel = Path.GetRelativePath(Path.Combine(resourceRoot, "stream"), path);
        var dest = Path.Combine(resourceRoot, "_not_used", RemovedFolder, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Move(path, dest, overwrite: true);
    }
}
