using System.Text;

namespace RpfToFiveM.Core.Extraction;

/// <summary>A support file (meta/xml/ini/audio data) extracted into the staging folder.</summary>
public sealed record StagedSupportFile(string StagedPath, string InnerPath, string Group);

public sealed class SupportResult
{
    public List<DataFileEntry> DataFiles { get; } = new();
    public int Used { get; set; }
    public int NotUsed { get; set; }
    public int Review { get; set; }
    public string? ReportPath { get; set; }
}

/// <summary>
/// Sorts extracted support files into the FiveM resource: useful ones into data/ (and
/// registered in the manifest), streamed ones into stream/, everything else into
/// _not_used/ — and explains each decision in meta-report.txt.
/// </summary>
public static class SupportFileProcessor
{
    public const string ReportFileName = "meta-report.txt";
    public const string StagingFolder = ".support";

    /// <param name="claimStreamName">Reserves a name in stream/ (FiveM needs unique names); false if taken.</param>
    public static SupportResult Process(string resourceRoot, IReadOnlyList<StagedSupportFile> files,
        Func<string, bool> claimStreamName, Action<LogEntry>? log)
    {
        var result = new SupportResult();
        var present = files.Where(f => File.Exists(f.StagedPath)).ToList();

        // The DLC's content.xml says what each data file is.
        var contentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in present.Where(f => Path.GetFileName(f.StagedPath).Equals("content.xml", StringComparison.OrdinalIgnoreCase)))
            foreach (var e in ContentXmlFile.Parse(File.ReadAllBytes(f.StagedPath)))
                if (!string.IsNullOrEmpty(e.FileType)) contentTypes.TryAdd(e.FileName, e.FileType);

        var lines = new List<(SupportKind Kind, string Text)>();
        foreach (var f in present)
        {
            var decision = SupportFiles.Classify(f.InnerPath, File.ReadAllBytes(f.StagedPath), contentTypes);
            var inner = Rel(f.InnerPath);
            string target;
            switch (decision.Kind)
            {
                case SupportKind.DataFile:
                    target = Path.Combine("data", f.Group, inner);
                    var filePath = target.Replace('\\', '/');
                    result.DataFiles.Add(new DataFileEntry(decision.DataFileType!, SupportFiles.DataFilePath(filePath), filePath));
                    result.Used++;
                    break;
                case SupportKind.Stream when claimStreamName(Path.GetFileName(f.StagedPath)):
                    target = Path.Combine("stream", f.Group, Path.GetFileName(f.StagedPath));
                    result.Used++;
                    break;
                case SupportKind.Stream:
                    decision = decision with { Kind = SupportKind.NotUsed, Reason = "duplicate name already streamed" };
                    target = Path.Combine("_not_used", f.Group, inner);
                    result.NotUsed++;
                    break;
                case SupportKind.Review:
                    target = Path.Combine("_not_used", f.Group, inner);
                    result.Review++;
                    break;
                default:
                    target = Path.Combine("_not_used", f.Group, inner);
                    result.NotUsed++;
                    break;
            }

            Move(f.StagedPath, Path.Combine(resourceRoot, target));
            var type = decision.DataFileType is null ? "" : $" [{decision.DataFileType}]";
            lines.Add((decision.Kind, $"{f.Group.Replace('\\', '/')}/{f.InnerPath}{type} - {decision.Reason} -> {target.Replace('\\', '/')}"));
        }

        TryDeleteDirectory(Path.Combine(resourceRoot, StagingFolder));
        result.ReportPath = WriteReport(resourceRoot, lines);

        if (present.Count > 0)
            log?.Invoke(new LogEntry(result.Review > 0 ? LogLevel.Warning : LogLevel.Info,
                $"Meta/xml/ini files: {result.Used} used, {result.NotUsed} not needed, {result.Review} to look at. See {ReportFileName}."));
        return result;
    }

    private static string Rel(string innerPath) => Rpf.SafePath.Relative(innerPath);

    private static void Move(string from, string to)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to, overwrite: true);
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string WriteReport(string resourceRoot, List<(SupportKind Kind, string Text)> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Meta / XML / INI / audio data files");
        sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}.");
        sb.AppendLine();

        void Section(SupportKind kind, string title)
        {
            var items = lines.Where(l => l.Kind == kind).Select(l => l.Text).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count == 0) return;
            sb.AppendLine($"{title} ({items.Count})");
            foreach (var i in items) sb.AppendLine("  " + i);
            sb.AppendLine();
        }

        Section(SupportKind.DataFile, "USED - registered in fxmanifest.lua");
        Section(SupportKind.Stream, "USED - streamed");
        Section(SupportKind.Review, "WORTH A LOOK - kept in _not_used");
        Section(SupportKind.NotUsed, "NOT NEEDED FOR A MAP - kept in _not_used");

        if (lines.Count == 0) sb.AppendLine("No meta, xml, ini or audio data files were found.");

        var path = Path.Combine(resourceRoot, ReportFileName);
        Directory.CreateDirectory(resourceRoot);
        File.WriteAllText(path, sb.ToString());
        return path;
    }
}
