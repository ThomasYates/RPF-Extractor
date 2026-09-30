using System.Text;
using System.Text.Json;
using RpfToFiveM.Core.Crypto;
using RpfToFiveM.Core.Rpf;

namespace RpfToFiveM.Core.Maps;

/// <summary>
/// What exists in the base game: texture dictionary names (so a model using vanilla
/// textures isn't mistaken for an untextured grey box) and model names (so reports
/// are readable and vanilla LOD models can be recognised). Built from archive tables
/// of contents only, so no file data is read.
/// </summary>
public sealed class GameAssetIndex
{
    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ydr", ".ydd", ".yft" };

    public HashSet<uint> TextureDictionaries { get; } = new();
    public Dictionary<uint, string> Names { get; } = new();

    public static GameAssetIndex Build(string gameFolder, GtaKeys? keys, CancellationToken ct)
    {
        var index = new GameAssetIndex();
        var archives = Directory.EnumerateFiles(gameFolder, "*.rpf",
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });

        foreach (var path in archives)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                index.Collect(RpfArchive.Open(fs, Path.GetFileName(path), keys), ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or RpfKeysRequiredException or UnauthorizedAccessException)
            {
                // Unreadable archives just contribute nothing.
            }
        }
        return index;
    }

    /// <summary>Loads the index from <paramref name="cacheFile"/> if it matches this install, else builds and caches it.</summary>
    public static GameAssetIndex LoadOrBuild(string gameFolder, GtaKeys? keys, string cacheFile, CancellationToken ct)
    {
        string stamp = Stamp(gameFolder);
        try
        {
            if (File.Exists(cacheFile))
            {
                var cached = JsonSerializer.Deserialize<Cache>(File.ReadAllText(cacheFile));
                if (cached is not null && cached.Stamp == stamp)
                {
                    var loaded = new GameAssetIndex();
                    loaded.TextureDictionaries.UnionWith(cached.TextureDictionaries);
                    foreach (var name in cached.Names) loaded.Names.TryAdd(Hash(name), name);
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }

        var index = Build(gameFolder, keys, ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(
                new Cache(stamp, index.TextureDictionaries.ToArray(), index.Names.Values.ToArray())));
        }
        catch (IOException) { }
        return index;
    }

    private sealed record Cache(string Stamp, uint[] TextureDictionaries, string[] Names);

    private void Collect(RpfArchive archive, CancellationToken ct)
    {
        foreach (var file in archive.Files)
        {
            if (file is RpfBinaryEntry { IsArchive: true } nested)
            {
                ct.ThrowIfCancellationRequested();
                try { Collect(archive.OpenNested(nested), ct); }
                catch (Exception ex) when (ex is InvalidDataException or IOException or RpfKeysRequiredException) { }
                continue;
            }

            var ext = Path.GetExtension(file.Name);
            var name = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();
            if (ext.Equals(".ytd", StringComparison.OrdinalIgnoreCase))
                TextureDictionaries.Add(Hash(name));
            else if (ModelExtensions.Contains(ext))
                Names.TryAdd(Hash(name), name);
        }
    }

    private static uint Hash(string name) => JenkHash.Hash(Encoding.ASCII.GetBytes(name));

    // Changes whenever the game is updated.
    private static string Stamp(string gameFolder)
    {
        var exe = GameLocator.FindExecutable(gameFolder);
        var info = exe is null ? null : new FileInfo(exe);
        return $"{Path.GetFullPath(gameFolder)}|{info?.Length}|{info?.LastWriteTimeUtc.Ticks}";
    }
}
