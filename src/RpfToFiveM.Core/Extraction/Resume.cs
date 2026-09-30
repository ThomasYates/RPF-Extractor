using System.Text;
using System.Text.Json;

namespace RpfToFiveM.Core.Extraction;

/// <summary>Records which output files are complete so an interrupted run can pick up where it stopped.</summary>
public interface IProgressJournal
{
    int CompletedCount { get; }
    bool IsCompleted(string key);
    void MarkCompleted(string key);
}

/// <summary>
/// Append-only journal on disk. Each finished file is written as one line ending in a
/// marker, and flushed immediately, so a crash loses at most the file being written.
/// </summary>
public sealed class FileJournal : IProgressJournal, IDisposable
{
    private const char EndMarker = '\u001F'; // lines without it were torn by a crash
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly StreamWriter _writer;

    public FileJournal(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
                if (line.Length > 1 && line[^1] == EndMarker)
                    _completed.Add(line[..^1]);
        }

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        // Start on a fresh line in case the previous run died mid-line.
        if (stream.Length > 0) _writer.WriteLine();
    }

    public int CompletedCount
    {
        get { lock (_completed) return _completed.Count; }
    }

    public bool IsCompleted(string key)
    {
        lock (_completed) return _completed.Contains(key);
    }

    public void MarkCompleted(string key)
    {
        lock (_completed)
        {
            if (!_completed.Add(key)) return;
            _writer.Write(key);
            _writer.Write(EndMarker);
            _writer.WriteLine();
        }
    }

    public void Dispose() => _writer.Dispose();

    /// <summary>Counts completed entries without holding the file open.</summary>
    public static int Count(string path)
    {
        if (!File.Exists(path)) return 0;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (line.Length > 1 && line[^1] == EndMarker) keys.Add(line[..^1]);
        return keys.Count;
    }
}

/// <summary>The settings of a run, saved so it can be resumed after a crash.</summary>
public sealed class JobInfo
{
    public required string SourceFolder { get; init; }
    public required string OutputFolder { get; init; }
    public string GameFolder { get; init; } = "";
    public OutputMode Mode { get; init; }
    public string ResourceName { get; init; } = "my_map";
    public Maps.ZFightMode ZFight { get; init; }
    public int FilesTotal { get; set; }
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Keeps the single unfinished job (settings + journal) in a folder.</summary>
public sealed class JobStore
{
    private readonly string _dir;
    private string JobPath => Path.Combine(_dir, "job.json");
    private string JournalPath => Path.Combine(_dir, "completed.log");

    public JobStore(string directory) => _dir = directory;

    public JobInfo? Load()
    {
        try
        {
            return File.Exists(JobPath) ? JsonSerializer.Deserialize<JobInfo>(File.ReadAllText(JobPath)) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(JobInfo job)
    {
        Directory.CreateDirectory(_dir);
        // Write-then-rename so a crash can't leave a half-written job file.
        var temp = JobPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(job, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, JobPath, overwrite: true);
    }

    public FileJournal OpenJournal() => new(JournalPath);

    public int CompletedCount() => FileJournal.Count(JournalPath);

    public void Clear()
    {
        foreach (var path in new[] { JobPath, JournalPath, JobPath + ".tmp" })
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
