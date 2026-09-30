using System.Text.RegularExpressions;

namespace RpfToFiveM.Core.Crypto;

/// <summary>Finds GTA V installs so encrypted game archives can be opened.</summary>
public static partial class GameLocator
{
    private static readonly string[] ExecutableNames = { "GTA5.exe", "GTA5_Enhanced.exe" };

    private static readonly string[] GameFolderNames =
    {
        "Grand Theft Auto V", "Grand Theft Auto V Enhanced", "GTAV", "GTAV Enhanced", "GTA V", "GTA V Enhanced",
    };

    public static string? FindExecutable(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        return ExecutableNames.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
    }

    /// <summary>Returns install folders found in the usual Steam, Epic and Rockstar locations.</summary>
    public static IEnumerable<string> FindGameFolders()
    {
        var roots = new List<string>();
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var steam = Path.Combine(pf86, "Steam");
        roots.Add(Path.Combine(steam, "steamapps", "common"));
        roots.AddRange(ReadSteamLibraries(Path.Combine(steam, "steamapps", "libraryfolders.vdf"))
            .Select(l => Path.Combine(l, "steamapps", "common")));
        roots.Add(Path.Combine(pf, "Epic Games"));
        roots.Add(Path.Combine(pf, "Rockstar Games"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed))
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common"));

        return roots
            .SelectMany(r => GameFolderNames.Select(n => Path.Combine(r, n)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => FindExecutable(f) is not null)
            .ToList();
    }

    private static IEnumerable<string> ReadSteamLibraries(string vdfPath)
    {
        try
        {
            if (!File.Exists(vdfPath)) return Array.Empty<string>();
            return SteamPathRegex().Matches(File.ReadAllText(vdfPath))
                .Select(m => m.Groups[1].Value.Replace(@"\\", @"\"))
                .ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
    private static partial Regex SteamPathRegex();
}
