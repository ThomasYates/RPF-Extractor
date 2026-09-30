using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using RpfToFiveM.Core.Extraction;
using RpfToFiveM.Core.Logging;

namespace RpfToFiveM.App;

/// <summary>
/// logs.txt next to the exe (or in %AppData%\RpfToFiveM if that folder isn't writable).
/// Everything written is redacted so the file can be shared without exposing folder paths.
/// </summary>
public static class AppLog
{
    private static FileLog? _log;
    private static Func<IEnumerable<(string Path, string Label)>> _folders = () => Array.Empty<(string, string)>();
    private static LogRedactor? _cached;
    private static string _cachedKey = "";

    public static string? FilePath => _log?.FilePath;

    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "unknown";

    public static void Start()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RpfToFiveM");
        try
        {
            _log = FileLog.OpenWithFallback(new[]
            {
                Path.Combine(AppContext.BaseDirectory, "logs.txt"),
                Path.Combine(appData, "logs.txt"),
            }, Redactor);
        }
        catch (IOException)
        {
            return; // No log is better than no app.
        }

        _log.WriteRaw("");
        _log.WriteRaw($"==== RPF Extractor {Version} started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
        _log.WriteRaw($"Windows {Environment.OSVersion.Version}, {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.OSArchitecture}");
        _log.WriteRaw("Folder paths are hidden: <source>, <export> and <gta> are the folders picked in the app, <home> is the user folder.");
    }

    /// <summary>The folders to hide, read fresh on every write.</summary>
    public static void SetFolders(Func<IEnumerable<(string Path, string Label)>> folders) => _folders = folders;

    public static void Write(LogLevel level, string message) =>
        _log?.Write(level switch { LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "INF" }, message);

    public static void Exception(string context, Exception ex) => _log?.Write("ERR", $"{context}: {ex}");

    public static void Stop()
    {
        _log?.WriteRaw($"==== closed {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====");
        _log?.Dispose();
        _log = null;
    }

    private static LogRedactor Redactor()
    {
        var folders = _folders().ToList();
        folders.Add((AppContext.BaseDirectory, "<app>"));
        folders.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RpfToFiveM"), "<appdata>"));

        // Rebuilding the patterns is only needed when a folder changes.
        var key = string.Join("|", folders.Select(f => f.Path + "=" + f.Label));
        if (_cached is null || key != _cachedKey)
        {
            _cached = LogRedactor.ForCurrentUser(folders);
            _cachedKey = key;
        }
        return _cached;
    }
}
