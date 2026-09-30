using System.Net.Http.Json;
using System.Text;
using RpfToFiveM.Core.Maps;

namespace RpfToFiveM.Core.Extraction;

public enum PoolStatus
{
    Fine,

    /// <summary>Raise it with increase_pool_size.</summary>
    Increase,

    /// <summary>Needs more than FiveM allows; the line uses the maximum.</summary>
    CannotFit,

    /// <summary>Over (or nearly over) a pool FiveM doesn't let servers raise.</summary>
    NotRaisable,
}

public sealed record PoolUsage(
    string Pool,
    string Holds,
    int BaseGame,
    int MapAdds,
    int Projected,
    int DefaultSize,
    int? MaxIncrease,
    int RecommendedIncrease,
    PoolStatus Status,
    string Advice);

public sealed class PoolReport
{
    public required IReadOnlyList<PoolUsage> Pools { get; init; }

    /// <summary>Ready to paste into server.cfg; empty when nothing needs raising.</summary>
    public required string ServerCfgLines { get; init; }

    public bool UsedBuiltInBaseCounts { get; init; }
    public bool UsedBuiltInLimits { get; init; }

    public bool HasRecommendations => ServerCfgLines.Length > 0;
    public bool HasWarnings => Pools.Any(p => p.Status is PoolStatus.NotRaisable or PoolStatus.CannotFit);
}

/// <summary>
/// Estimates whether a FiveM map pushes the game's asset pools past their size, and
/// suggests <c>increase_pool_size</c> lines for server.cfg. Every streamed file takes a
/// slot in its store (unless it replaces a base-game file of the same name), and the base
/// game already uses a large share of each store.
/// </summary>
public static class PoolAdvisor
{
    public const string ReportFileName = "pool-sizes.txt";
    public const string LimitsUrl = "https://gss.cfx-services.net/v1/public/pool-size-limits/fivem";

    private const double RaiseAt = 0.90;   // recommend once a pool would be over 90% full
    private const double Headroom = 1.10;  // and leave 10% spare for other resources
    private const int RoundTo = 500;
    private const int MinInteriorsToMention = 100;

    private sealed record Store(string Pool, string Extension, int DefaultSize, string Holds);

    // Sizes from FiveM's own gameconfig.xml (citizenfx/fivem data/client/citizen/common/data).
    private static readonly Store[] Stores =
    {
        new("TxdStore", ".ytd", 105500, "texture dictionaries (.ytd)"),
        new("DrawableStore", ".ydr", 235300, "models (.ydr)"),
        new("DwdStore", ".ydd", 240000, "model dictionaries (.ydd)"),
        new("FragmentStore", ".yft", 60700, "fragment models (.yft)"),
        new("StaticBounds", ".ybn", 20200, "collisions (.ybn)"),
        new("MapTypesStore", ".ytyp", 3000, "archetype files (.ytyp)"),
        new("MapDataStore", ".ymap", MapDataStoreSize, "map placement files (.ymap)"),
    };

    private const int InteriorProxyDefault = 9060;

    /// <summary>MapDataStore (ymaps): FiveM doesn't let servers raise it.</summary>
    public const int MapDataStoreSize = 12000;

    /// <summary>Ymap slots to leave free for the server's other resources.</summary>
    public const int YmapSpare = 300;

    /// <summary>FiveM's server-side limits as of this release; the live list is fetched when possible.</summary>
    public static readonly IReadOnlyDictionary<string, int> BuiltInLimits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["TxdStore"] = 50000,
        ["FragmentStore"] = 30000,
        ["StaticBounds"] = 5000,
        ["InteriorProxy"] = 450,
    };

    /// <summary>Unique base-game files per type, measured on GTA V Legacy (used when no GTA V folder is set).</summary>
    public static readonly IReadOnlyDictionary<string, int> BuiltInBaseCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [".ytd"] = 40118,
        [".ydr"] = 82690,
        [".ydd"] = 10604,
        [".yft"] = 42040,
        [".ybn"] = 13926,
        [".ytyp"] = 1717,
        [".ymap"] = 11086,
    };

    /// <param name="mapNewCounts">Files the map adds per extension (excluding ones that replace base-game files).</param>
    /// <param name="baseGame">Base-game file counts per extension, or null to use the built-in Legacy counts.</param>
    /// <param name="limits">FiveM's allowed increases per pool, or null to use the built-in list.</param>
    /// <param name="mloInstances">Interiors (MLO instances) placed by the map.</param>
    public static PoolReport Analyze(IReadOnlyDictionary<string, int> mapNewCounts, IReadOnlyDictionary<string, int>? baseGame,
        IReadOnlyDictionary<string, int>? limits, int mloInstances)
    {
        var usedBuiltInBase = baseGame is null;
        var usedBuiltInLimits = limits is null;
        baseGame ??= BuiltInBaseCounts;
        limits ??= BuiltInLimits;
        var caseless = new Dictionary<string, int>(limits, StringComparer.OrdinalIgnoreCase);

        var pools = new List<PoolUsage>();
        foreach (var s in Stores)
        {
            int base_ = baseGame.TryGetValue(s.Extension, out var b) ? b : 0;
            int adds = mapNewCounts.TryGetValue(s.Extension, out var a) ? a : 0;
            pools.Add(Evaluate(s.Pool, s.Holds, base_, adds, s.DefaultSize, caseless));
        }

        // The base game's interior count isn't known from archive tables, so only the map's own interiors are judged.
        pools.Add(EvaluateInteriors(mloInstances, caseless));

        var lines = string.Join(Environment.NewLine, pools
            .Where(p => p.Status is PoolStatus.Increase or PoolStatus.CannotFit && p.RecommendedIncrease > 0)
            .Select(p => $"increase_pool_size \"{p.Pool}\" {p.RecommendedIncrease}"));

        return new PoolReport
        {
            Pools = pools,
            ServerCfgLines = lines,
            UsedBuiltInBaseCounts = usedBuiltInBase,
            UsedBuiltInLimits = usedBuiltInLimits,
        };
    }

    private static PoolUsage Evaluate(string pool, string holds, int baseGame, int adds, int size, Dictionary<string, int> limits)
    {
        int projected = baseGame + adds;
        if (projected <= size * RaiseAt || adds == 0)
            return new(pool, holds, baseGame, adds, projected, size, Max(limits, pool), 0, PoolStatus.Fine, "Enough room.");

        if (!limits.TryGetValue(pool, out var max))
        {
            // Nothing to paste for these, so only an actual overflow is worth a warning.
            if (projected <= size)
                return new(pool, holds, baseGame, adds, projected, size, null, 0, PoolStatus.Fine,
                    $"Fits, but close to the {size:N0} limit ({size - projected:N0} left for other resources), and this pool can't be raised in FiveM.");
            return new(pool, holds, baseGame, adds, projected, size, null, 0, PoolStatus.NotRaisable,
                $"Base game + this map is over the {size:N0} limit, and this pool can't be raised in FiveM. " +
                $"Reduce the {holds} the map adds (it adds {adds:N0}).");
        }

        int wanted = RoundUp((int)Math.Ceiling(projected * Headroom) - size);
        if (wanted <= max)
            return new(pool, holds, baseGame, adds, projected, size, max, wanted, PoolStatus.Increase,
                $"Raise it by {wanted:N0} (room for this map plus 10% for other resources).");

        return new(pool, holds, baseGame, adds, projected, size, max, max, PoolStatus.CannotFit,
            $"Needs about {wanted:N0} more, but FiveM's maximum increase is {max:N0}. Use the maximum and reduce the {holds} the map adds.");
    }

    private static PoolUsage EvaluateInteriors(int mlos, Dictionary<string, int> limits)
    {
        const string pool = "InteriorProxy";
        const string holds = "interiors (MLOs)";
        int? max = Max(limits, pool);
        if (mlos < MinInteriorsToMention)
            return new(pool, holds, 0, mlos, mlos, InteriorProxyDefault, max, 0, PoolStatus.Fine, "Enough room.");
        if (max is null)
            return new(pool, holds, 0, mlos, mlos, InteriorProxyDefault, null, 0, PoolStatus.NotRaisable,
                $"The map places {mlos:N0} interiors and FiveM doesn't currently let servers raise this pool.");

        int wanted = (int)Math.Ceiling(mlos * Headroom);
        int rounded = Math.Min(RoundUp(wanted), max.Value);
        return wanted <= max
            ? new(pool, holds, 0, mlos, mlos, InteriorProxyDefault, max, rounded, PoolStatus.Increase,
                $"The map places {mlos:N0} interiors; raise it to make room for them.")
            : new(pool, holds, 0, mlos, mlos, InteriorProxyDefault, max, max.Value, PoolStatus.CannotFit,
                $"The map places {mlos:N0} interiors, more than FiveM's maximum increase of {max:N0}.");
    }

    private static int? Max(Dictionary<string, int> limits, string pool) => limits.TryGetValue(pool, out var m) ? m : null;

    private static int RoundUp(int value) => Math.Max(RoundTo, (value + RoundTo - 1) / RoundTo * RoundTo);

    /// <summary>FiveM's current increase limits, or null when offline.</summary>
    public static async Task<IReadOnlyDictionary<string, int>?> FetchLimitsAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            return await http.GetFromJsonAsync<Dictionary<string, int>>(LimitsUrl, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public static string BuildReport(PoolReport r, TrimResult? trim = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("FiveM pool sizes");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}.");
        sb.AppendLine();
        sb.AppendLine("Every streamed file takes a slot in a game pool, and the base game already uses a lot of each.");
        sb.AppendLine("Base game counts: " + (r.UsedBuiltInBaseCounts ? "built-in GTA V Legacy counts (no GTA V folder set)." : "measured from your GTA V install."));
        sb.AppendLine("Increase limits: " + (r.UsedBuiltInLimits ? "built-in list (FiveM's live list couldn't be fetched)." : "FiveM's live list."));
        sb.AppendLine();

        if (r.HasRecommendations)
        {
            sb.AppendLine("ADD TO server.cfg (anywhere, before the resources start):");
            sb.AppendLine();
            sb.AppendLine(r.ServerCfgLines);
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("No server.cfg changes needed for this map on its own.");
            sb.AppendLine();
        }

        sb.AppendLine($"{"Pool",-15} {"Base game",10} {"Map adds",9} {"Total",9} {"Size",9}  Result");
        foreach (var p in r.Pools)
        {
            string status = p.Status switch
            {
                PoolStatus.Fine => "ok",
                PoolStatus.Increase => $"raise by {p.RecommendedIncrease:N0}",
                PoolStatus.CannotFit => $"raise by {p.RecommendedIncrease:N0} (max) - not enough",
                _ => "can't be raised",
            };
            sb.AppendLine($"{p.Pool,-15} {p.BaseGame,10:N0} {p.MapAdds,9:N0} {p.Projected,9:N0} {p.DefaultSize,9:N0}  {status}");
        }

        var notes = r.Pools.Where(p => p.Status != PoolStatus.Fine).ToList();
        if (notes.Count > 0)
        {
            sb.AppendLine();
            foreach (var p in notes) sb.AppendLine($"{p.Pool}: {p.Advice}");
        }

        sb.AppendLine();
        if (trim is not null && (trim.RemovedEmpty.Count + trim.RemovedGrass.Count > 0 || trim.StillOver))
        {
            sb.AppendLine();
            sb.Append(YmapTrimmer.Describe(trim));
        }

        sb.AppendLine();
        sb.AppendLine("Other resources on the server (cars, clothing, other maps) use these pools too. If the game");
        sb.AppendLine("reports \"Pool Full: <name>\", raise that pool further, up to FiveM's limit.");
        return sb.ToString();
    }
}
