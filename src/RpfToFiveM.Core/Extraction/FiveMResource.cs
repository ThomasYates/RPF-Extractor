using System.Text;
using System.Text.RegularExpressions;

namespace RpfToFiveM.Core.Extraction;

/// <summary>Rules for building a FiveM map resource.</summary>
public static partial class FiveMResource
{
    /// <summary>Streamable asset types that make up a map.</summary>
    private static readonly HashSet<string> MapExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ymap", // placements
        ".ytyp", // archetypes / MLO interiors
        ".ydr",  // drawables
        ".ydd",  // drawable dictionaries
        ".yft",  // fragments
        ".ytd",  // textures
        ".ybn",  // collisions
        ".ynv",  // navmeshes
        ".ynd",  // path nodes
        ".ycd",  // clip dictionaries (animated props)
    };

    public static bool IsMapFile(string fileName) => MapExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// True for folders/archives holding peds, clothing, vehicles, weapons, animations or audio.
    /// Those use the same file types as maps but are not map content (and ped clothing
    /// reuses the same file names across packs, which FiveM can't stream side by side).
    /// </summary>
    /// <param name="folderPath">'/' separated path of archives and directories, without the file name.</param>
    public static bool IsNonMapFolder(string folderPath) =>
        folderPath.Split('/', '\\').Any(segment => NonMapSegment().IsMatch(Path.GetFileNameWithoutExtension(segment)));

    /// <summary>Resource versions used by GTA V Enhanced, which FiveM (legacy based) can't load.</summary>
    private static readonly Dictionary<string, uint> Gen9Versions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ydr"] = 159,
        [".ydd"] = 159,
        [".yft"] = 171,
        [".ytd"] = 5,
    };

    public static bool IsGen9Resource(string fileName, uint version) =>
        Gen9Versions.TryGetValue(Path.GetExtension(fileName), out var v) && v == version;

    public static bool IsYtyp(string fileName) =>
        string.Equals(Path.GetExtension(fileName), ".ytyp", StringComparison.OrdinalIgnoreCase);

    public static string SanitizeResourceName(string name)
    {
        var s = InvalidResourceChars().Replace(name.Trim().ToLowerInvariant(), "_").Trim('_');
        s = RepeatedUnderscores().Replace(s, "_");
        return s.Length == 0 ? "my_map" : s;
    }

    /// <param name="ytypPaths">Resource-relative paths of every streamed .ytyp.</param>
    /// <param name="dataFiles">Extra data files (texture parenting, audio, ...) to send and register.</param>
    public static string BuildManifest(IEnumerable<string> ytypPaths, IEnumerable<DataFileEntry>? dataFiles = null)
    {
        static string Q(string s) => s.Replace('\\', '/').Replace("'", "\\'");

        var sb = new StringBuilder();
        sb.AppendLine("fx_version 'cerulean'");
        sb.AppendLine("game 'gta5'");
        sb.AppendLine();
        sb.AppendLine("this_is_a_map 'yes'");

        var extras = (dataFiles ?? Enumerable.Empty<DataFileEntry>())
            .OrderBy(d => d.Type, StringComparer.Ordinal).ThenBy(d => d.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
        if (extras.Count > 0)
        {
            // Data files must be listed in files {} so clients download them.
            sb.AppendLine();
            sb.AppendLine("files {");
            foreach (var d in extras) sb.AppendLine($"    '{Q(d.FilePath)}',");
            sb.AppendLine("}");
            sb.AppendLine();
            foreach (var d in extras) sb.AppendLine($"data_file '{d.Type}' '{Q(d.ManifestPath)}'");
        }

        var ytyps = ytypPaths.Select(p => p.Replace('\\', '/')).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        if (ytyps.Count > 0)
        {
            sb.AppendLine();
            foreach (var p in ytyps)
                sb.AppendLine($"data_file 'DLC_ITYP_REQUEST' '{p.Replace("'", "\\'")}'");
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"ped|freemode|(^|_)(fe)?male(_|$)|^vehicle|^weapon|^anims?$|^cutscene|^audio$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NonMapSegment();

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex InvalidResourceChars();

    [GeneratedRegex("_{2,}")]
    private static partial Regex RepeatedUnderscores();
}
