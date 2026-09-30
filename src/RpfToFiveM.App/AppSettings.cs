using System.IO;
using System.Text.Json;

namespace RpfToFiveM.App;

/// <summary>User preferences persisted between runs in %AppData%\RpfToFiveM.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RpfToFiveM", "settings.json");

    public string SourceFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string GameFolder { get; set; } = "";
    public bool FiveMMode { get; set; } = true;
    public string ResourceName { get; set; } = "my_map";
    public RpfToFiveM.Core.Maps.ZFightMode ZFight { get; set; } = RpfToFiveM.Core.Maps.ZFightMode.Off;

    /// <summary>AES key found in the game exe last time, so the exe need not be rescanned.</summary>
    public string CachedAesKey { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file just means starting fresh.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save must not break the app.
        }
    }
}
