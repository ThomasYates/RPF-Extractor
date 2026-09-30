using System.Text;

namespace RpfToFiveM.Core.Logging;

/// <summary>
/// Appends timestamped, redacted log lines to a text file (flushed per line so nothing is
/// lost if the app crashes). The file is rotated to *.old.txt once it gets large.
/// </summary>
public sealed class FileLog : IDisposable
{
    public const long DefaultMaxBytes = 5 * 1024 * 1024;

    private readonly Func<LogRedactor> _redactor;
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public string FilePath { get; }

    /// <param name="redactor">Called per write, so redaction follows the folders currently in use.</param>
    public FileLog(string path, Func<LogRedactor> redactor, long maxBytes = DefaultMaxBytes)
    {
        FilePath = Path.GetFullPath(path);
        _redactor = redactor;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        if (File.Exists(FilePath) && new FileInfo(FilePath).Length > maxBytes)
            File.Move(FilePath, Path.ChangeExtension(FilePath, null) + ".old.txt", overwrite: true);

        var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
    }

    /// <summary>Opens the first location that can be written to.</summary>
    public static FileLog OpenWithFallback(IEnumerable<string> paths, Func<LogRedactor> redactor, long maxBytes = DefaultMaxBytes)
    {
        Exception? last = null;
        foreach (var path in paths)
        {
            try { return new FileLog(path, redactor, maxBytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                last = ex;
            }
        }
        throw new IOException("No writable location for the log file.", last);
    }

    public void Write(string level, string message)
    {
        var redactor = _redactor();
        var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        lock (_gate)
        {
            // Multi-line messages (stack traces) keep one prefix per line.
            foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
                _writer.WriteLine($"{time}  {level}  {redactor.Redact(line)}");
        }
    }

    /// <summary>Writes a line without a timestamp (session headers), still redacted.</summary>
    public void WriteRaw(string text)
    {
        var redactor = _redactor();
        lock (_gate) _writer.WriteLine(redactor.Redact(text));
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }
}
