using RpfToFiveM.Core.Crypto;
using RpfToFiveM.Core.Maps;

namespace RpfToFiveM.Core.Extraction;

public enum OutputMode
{
    /// <summary>Mirror every archive as a folder, keeping the original layout.</summary>
    Dump,

    /// <summary>Collect map assets into a drag-and-drop FiveM resource.</summary>
    FiveM,
}

public enum ExtractionPhase
{
    Scanning,
    Extracting,

    /// <summary>Writing fxmanifest.lua and sorting meta files.</summary>
    Finishing,

    /// <summary>Copying the output for the z-fixed version (z-fight "Both").</summary>
    CopyingForZFix,

    CheckingZFight,
    Completed,
}

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(LogLevel Level, string Message)
{
    public DateTime Time { get; } = DateTime.Now;
}

public sealed class ExtractionOptions
{
    public required string SourceFolder { get; init; }
    public required string OutputFolder { get; init; }
    public OutputMode Mode { get; init; } = OutputMode.Dump;
    public string ResourceName { get; init; } = "my_map";

    /// <summary>Needed only for encrypted (retail game) archives.</summary>
    public GtaKeys? Keys { get; init; }

    /// <summary>When set, finished files are recorded here and skipped on a later resume.</summary>
    public IProgressJournal? Journal { get; init; }

    /// <summary>Whether to fix overlapping duplicate buildings, and whether to keep an untouched copy too.</summary>
    public ZFightMode ZFight { get; init; } = ZFightMode.Off;

    /// <summary>Base-game textures and model names, so vanilla-textured models aren't treated as grey.</summary>
    public GameAssetIndex? GameAssets { get; init; }

    /// <summary>FiveM's allowed pool increases (fetched live by the app); null uses the built-in list.</summary>
    public IReadOnlyDictionary<string, int>? PoolLimits { get; init; }

    /// <summary>Free bytes on the drive holding a path (null = unknown). Replaceable for tests.</summary>
    public Func<string, long?> FreeSpace { get; init; } = DefaultFreeSpace;

    private static long? DefaultFreeSpace(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
}

public sealed record ExtractionProgress(
    ExtractionPhase Phase,
    long BytesDone,
    long BytesTotal,
    int FilesDone,
    int FilesTotal,
    string CurrentItem)
{
    public double Fraction => BytesTotal <= 0 ? (Phase == ExtractionPhase.Completed ? 1 : 0) : (double)BytesDone / BytesTotal;
}

public sealed class ExtractionResult
{
    public required string OutputRoot { get; init; }

    /// <summary>The z-fight-fixed copy when <see cref="ZFightMode.Both"/> was used.</summary>
    public string? FixedOutputRoot { get; init; }
    public int FilesWritten { get; init; }
    public int ErrorCount { get; init; }
    public int DuplicatesSkipped { get; init; }

    /// <summary>Files already finished by an earlier, interrupted run.</summary>
    public int FilesResumed { get; init; }
    public long BytesRead { get; init; }

    /// <summary>Pool size estimate and server.cfg advice (FiveM mode only).</summary>
    public PoolReport? Pools { get; init; }
}
