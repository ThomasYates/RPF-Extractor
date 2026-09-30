using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace RpfToFiveM.Core.Extraction;

public enum SupportKind
{
    /// <summary>Registered in fxmanifest.lua with a data_file entry.</summary>
    DataFile,

    /// <summary>Streamed like a map asset.</summary>
    Stream,

    /// <summary>Not needed for a map (kept aside).</summary>
    NotUsed,

    /// <summary>Unrecognised or needs manual work (kept aside).</summary>
    Review,
}

public sealed record SupportDecision(SupportKind Kind, string? DataFileType, string Reason);

/// <summary>One data_file line plus the file it needs listed in files {}.</summary>
public sealed record DataFileEntry(string Type, string ManifestPath, string FilePath);

/// <summary>A DLC's content.xml entry: which file, what type, and whether it's loaded only on demand.</summary>
public sealed record ContentXmlEntry(string FileName, string FileType, bool Disabled);

/// <summary>Decides whether .meta / .xml / .ini / audio data files are useful for a FiveM map.</summary>
public static partial class SupportFiles
{
    private static readonly HashSet<string> CandidateExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".meta", ".xml", ".ini", ".rel", ".ymt", ".ymf",
    };

    /// <summary>data_file types that matter for maps and that FiveM accepts.</summary>
    private static readonly Dictionary<string, string> MapDataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GTXD_PARENTING_DATA"] = "texture parenting: lets models find textures in parent dictionaries (missing = grey/missing textures)",
        ["INTERIOR_PROXY_ORDER_FILE"] = "interior (MLO) load order",
        ["TIMECYCLEMOD_FILE"] = "timecycle modifiers: custom lighting/fog for areas or interiors",
        ["AUDIO_GAMEDATA"] = "audio game data (interior rooms, ambient zones, emitters)",
        ["AUDIO_DYNAMIXDATA"] = "audio mix data",
        ["AUDIO_SOUNDDATA"] = "audio sound definitions",
        ["AUDIO_SYNTHDATA"] = "audio synth data",
    };

    // Root XML element -> data_file type.
    private static readonly Dictionary<string, string> RootToType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CMapParentTxds"] = "GTXD_PARENTING_DATA",
        ["SInteriorOrderData"] = "INTERIOR_PROXY_ORDER_FILE",
        ["timecycle_modifier_data"] = "TIMECYCLEMOD_FILE",
    };

    // Audio .rel files are identified by their "datNN" suffix.
    private static readonly Dictionary<string, string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".dat151.rel"] = "AUDIO_GAMEDATA",
        [".dat15.rel"] = "AUDIO_DYNAMIXDATA",
        [".dat54.rel"] = "AUDIO_SOUNDDATA",
        [".dat10.rel"] = "AUDIO_SYNTHDATA",
    };

    // Known non-map files, by root element or name.
    private static readonly Dictionary<string, string> NotForMaps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CDataFileMgr__ContentsOfDataFileXml"] = "DLC content list; FiveM uses fxmanifest.lua instead (read for this report)",
        ["SSetupData"] = "DLC setup file; FiveM uses fxmanifest.lua instead",
        ["CVehicleModelInfo__InitDataList"] = "vehicle definitions, not map content",
        ["CHandlingDataMgr"] = "vehicle handling, not map content",
        ["CVehicleModelInfoVarGlobal"] = "vehicle colours (carcols), not map content",
        ["CVehicleModelInfoVariation"] = "vehicle variations, not map content",
        ["CPedModelInfo__InitDataList"] = "ped definitions, not map content",
        ["CWeaponInfoBlob"] = "weapon definitions, not map content",
        ["CWeaponComponentInfoBlob"] = "weapon components, not map content",
        ["CWeaponAnimationsSets"] = "weapon animations, not map content",
        ["CExtraTextMetaFile"] = "DLC text registration, handled by FiveM",
        ["CZonedAssets"] = "streaming hints for the base game, not needed",
        ["CShopPedApparel"] = "clothing shop data, not map content",
        ["ShopPedApparel"] = "clothing shop data, not map content",
    };

    public static bool IsCandidate(string fileName) =>
        CandidateExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>Resource paths for audio data drop the ".NNN.rel" part (file "x.dat151.rel" is registered as "x.dat").</summary>
    public static string DataFilePath(string resourcePath) => AudioSuffix().Replace(resourcePath, ".dat");

    /// <param name="innerPath">'/' path inside the archives, e.g. "dlc.rpf/common/data/gtxd.meta".</param>
    /// <param name="contentTypes">file name -> data_file type, from the DLC's content.xml.</param>
    public static SupportDecision Classify(string innerPath, byte[] content, IReadOnlyDictionary<string, string>? contentTypes)
    {
        var name = Path.GetFileName(innerPath);
        var lower = name.ToLowerInvariant();

        // The DLC's own manifest is the most reliable answer.
        if (contentTypes is not null && contentTypes.TryGetValue(name, out var listed))
        {
            if (MapDataTypes.TryGetValue(listed, out var what))
                return new(SupportKind.DataFile, listed, $"{what} (listed in content.xml)");
            if (!listed.Equals("DLC_ITYP_REQUEST", StringComparison.OrdinalIgnoreCase))
                return new(SupportKind.NotUsed, null, $"content.xml says {listed}, which isn't map data");
        }

        foreach (var (suffix, type) in AudioTypes)
            if (lower.EndsWith(suffix))
                return new(SupportKind.DataFile, type, MapDataTypes[type]);

        if (lower.EndsWith(".dat4.rel"))
            return new(SupportKind.NotUsed, null, "speech audio data, not map content");
        if (lower.EndsWith(".rel"))
            return new(SupportKind.Review, null, "audio data of an unknown kind");

        if (lower.EndsWith(".ini"))
            return new(SupportKind.NotUsed, null, "FiveM doesn't read .ini files (usually installer/config leftovers)");

        if (lower.EndsWith(".ymf"))
            return new(SupportKind.NotUsed, null, "DLC streaming manifest; FiveM builds its own");

        if (lower.EndsWith(".ymt"))
        {
            // Interior audio occlusion files are named by a number (a hash).
            if (NumericName().IsMatch(Path.GetFileNameWithoutExtension(name)))
                return new(SupportKind.Stream, null, "interior audio occlusion (streamed)");
            if (lower.EndsWith("_srl.ymt"))
                return new(SupportKind.NotUsed, null, "cutscene/mission streaming request list, not map content");
            if (lower.StartsWith("bink_"))
                return new(SupportKind.NotUsed, null, "TV/cinema video metadata, not map content");
            if (FiveMResource.IsNonMapFolder(FolderOf(innerPath)))
                return new(SupportKind.NotUsed, null, "ped/vehicle metadata, not map content");
            return new(SupportKind.Review, null, "metadata (.ymt) such as scenarios or doors; needs setting up by hand if wanted");
        }

        var root = RootElement(content);
        if (lower is "gtxd.meta") return new(SupportKind.DataFile, "GTXD_PARENTING_DATA", MapDataTypes["GTXD_PARENTING_DATA"]);
        if (lower is "interiorproxies.meta") return new(SupportKind.DataFile, "INTERIOR_PROXY_ORDER_FILE", MapDataTypes["INTERIOR_PROXY_ORDER_FILE"]);

        if (root is not null)
        {
            if (RootToType.TryGetValue(root, out var type)) return new(SupportKind.DataFile, type, MapDataTypes[type]);
            if (NotForMaps.TryGetValue(root, out var why)) return new(SupportKind.NotUsed, null, why);
            if (root is "CMapData" or "CMapTypes")
                return new(SupportKind.Review, null, $"CodeWalker XML of a {(root == "CMapData" ? "ymap" : "ytyp")}; import it in CodeWalker and save as binary to use it");
        }

        if (FiveMResource.IsNonMapFolder(FolderOf(innerPath)))
            return new(SupportKind.NotUsed, null, "in a ped/vehicle/weapon/animation/audio folder");

        return new(SupportKind.Review, null, root is null
            ? "unrecognised (binary or unreadable) data"
            : $"unrecognised data <{root}>");
    }

    /// <summary>The first element of an XML document, or null for binary/non-XML data.</summary>
    internal static string? RootElement(byte[] content)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
            using var reader = XmlReader.Create(new MemoryStream(content), settings);
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element) return reader.LocalName;
        }
        catch (XmlException) { }
        catch (DecoderFallbackException) { }
        return null;
    }

    private static string FolderOf(string innerPath)
    {
        int slash = innerPath.LastIndexOf('/');
        return slash < 0 ? "" : innerPath[..slash];
    }

    [GeneratedRegex(@"\.dat\d+\.rel$", RegexOptions.IgnoreCase)]
    private static partial Regex AudioSuffix();

    [GeneratedRegex(@"^-?\d+$")]
    private static partial Regex NumericName();
}

/// <summary>Reads a DLC's content.xml (CDataFileMgr__ContentsOfDataFileXml).</summary>
public static class ContentXmlFile
{
    public static IReadOnlyList<ContentXmlEntry> Parse(byte[] content)
    {
        var entries = new List<ContentXmlEntry>();
        if (SupportFiles.RootElement(content) != "CDataFileMgr__ContentsOfDataFileXml") return entries;

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new MemoryStream(content), settings);
            var doc = new XmlDocument { XmlResolver = null };
            doc.Load(reader);
            var items = doc.SelectNodes("/CDataFileMgr__ContentsOfDataFileXml/dataFiles/Item");
            if (items is null) return entries;

            foreach (XmlNode item in items)
            {
                var file = item.SelectSingleNode("filename")?.InnerText?.Trim();
                var type = item.SelectSingleNode("fileType")?.InnerText?.Trim() ?? "";
                if (string.IsNullOrEmpty(file)) continue;
                bool disabled = string.Equals(item.SelectSingleNode("disabled")?.Attributes?["value"]?.Value, "true", StringComparison.OrdinalIgnoreCase);
                var name = file.Replace('\\', '/').Split('/').Last();
                entries.Add(new ContentXmlEntry(name, type, disabled));
            }
        }
        catch (XmlException) { }
        return entries;
    }
}
