using System.Diagnostics;
using RpfToFiveM.Core.Maps;
using RpfToFiveM.Core.Rpf;

namespace RpfToFiveM.Core.Extraction;

/// <summary>
/// Extracts every RPF under a folder (recursing into nested archives), either as a
/// mirrored folder tree or as a FiveM map resource.
/// </summary>
public sealed class RpfExtractor
{
    /// <summary>Folder created inside the export location for folder dumps.</summary>
    public const string DumpFolderName = "extracted";

    public Task<ExtractionResult> RunAsync(
        ExtractionOptions options,
        IProgress<ExtractionProgress>? progress,
        Action<LogEntry>? log,
        PauseController pause,
        CancellationToken ct = default)
    {
        return Task.Run(() => new Session(options, progress, log, pause, ct).Run(), ct);
    }

    /// <summary>
    /// One run. Archives are walked twice with the same logic: a scan pass that only
    /// totals sizes (so progress is accurate) and an extract pass that writes files.
    /// </summary>
    private sealed class Session
    {
        private const int MaxDuplicateLogs = 25;
        private const string LooseGroup = "_loose";
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(50);

        private readonly ExtractionOptions _options;
        private readonly IProgress<ExtractionProgress>? _progress;
        private readonly Action<LogEntry>? _log;
        private readonly PauseController _pause;
        private readonly CancellationToken _ct;
        private readonly string _source;
        private readonly string _outputRoot;
        private readonly string? _fixedRoot;
        private readonly bool _fiveM;
        private readonly IProgressJournal? _journal;

        private readonly HashSet<string> _claimedNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _failedArchives = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _ytypPaths = new();
        private readonly List<StagedSupportFile> _supportFiles = new();
        private readonly Stopwatch _sinceReport = Stopwatch.StartNew();

        private bool _scanning;
        private long _bytesTotal, _bytesDone;
        private int _filesTotal, _filesDone, _filesWritten, _errors, _duplicates, _nonMapSkipped, _gen9Count, _filesResumed;
        private string _current = "";

        public Session(ExtractionOptions options, IProgress<ExtractionProgress>? progress, Action<LogEntry>? log,
            PauseController pause, CancellationToken ct)
        {
            _options = options;
            _progress = progress;
            _log = log;
            _pause = pause;
            _ct = ct;
            _source = Path.GetFullPath(options.SourceFolder);
            _fiveM = options.Mode == OutputMode.FiveM;
            _journal = options.Journal;
            var output = Path.GetFullPath(options.OutputFolder);
            var resource = FiveMResource.SanitizeResourceName(options.ResourceName);
            bool both = options.ZFight == ZFightMode.Both;

            // "Both" writes an untouched copy plus a z-fixed one side by side.
            if (_fiveM)
            {
                _outputRoot = Path.Combine(output, resource);
                // FiveM resource names can't contain spaces (server.cfg "ensure" breaks), so no " (ZFightFix)" here.
                _fixedRoot = both ? Path.Combine(output, resource + "_zfix") : null;
            }
            else
            {
                // Dumps go in their own folder rather than spilling into the chosen location.
                _outputRoot = Path.Combine(output, DumpFolderName);
                _fixedRoot = both ? Path.Combine(output, DumpFolderName + " (ZFightFix)") : null;
            }
        }

        public ExtractionResult Run()
        {
            if (!Directory.Exists(_source))
                throw new DirectoryNotFoundException($"Source folder not found: {_source}");

            var archives = FindFiles("*.rpf").OrderBy(ArchivePriority).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            var looseMaps = _fiveM ? FindFiles("*").Where(FiveMResource.IsMapFile).Order(StringComparer.OrdinalIgnoreCase).ToList() : new();

            Log(LogLevel.Info, $"Found {archives.Count} archive(s) in {_source}.");
            if (archives.Count == 0 && looseMaps.Count == 0)
                Log(LogLevel.Warning, "No .rpf files were found in the source folder.");

            // Pass 1: totals.
            _scanning = true;
            for (int i = 0; i < archives.Count; i++)
            {
                Checkpoint();
                _current = $"Scanning {Relative(archives[i])} ({i + 1}/{archives.Count})";
                Report(force: true);
                ProcessArchive(archives[i]);
            }
            foreach (var loose in looseMaps) ProcessLoose(loose);
            Log(LogLevel.Info, $"Scan complete: {_filesTotal:N0} file(s), {_bytesTotal / 1048576.0:N1} MB to extract.");

            // Pass 2: extraction.
            _scanning = false;
            _claimedNames.Clear();
            foreach (var archive in archives.Where(a => !_failedArchives.Contains(a)))
            {
                Checkpoint();
                ProcessArchive(archive);
            }
            foreach (var loose in looseMaps) ProcessLoose(loose);

            if (_fiveM)
            {
                var support = SupportFileProcessor.Process(_outputRoot, _supportFiles, _claimedNames.Add, _log);
                WriteManifest(support.DataFiles);
            }

            if (_filesResumed > 0)
                Log(LogLevel.Info, $"Resumed: {_filesResumed:N0} file(s) were already done from the interrupted run.");
            if (_nonMapSkipped > 0)
                Log(LogLevel.Info, $"Left out {_nonMapSkipped:N0} non-map file(s) (peds/clothing, vehicles, weapons, animations, audio).");
            if (_gen9Count > 0)
                Log(LogLevel.Warning, $"{_gen9Count:N0} file(s) are in the GTA V Enhanced (gen9) format, which FiveM can't load. " +
                    "Extract from a Legacy GTA V install, or convert them with CodeWalker's Gen9 converter.");
            if (_duplicates > MaxDuplicateLogs)
                Log(LogLevel.Warning, $"{_duplicates:N0} duplicate file name(s) skipped in total (FiveM needs unique names).");
            Log(_errors == 0 ? LogLevel.Info : LogLevel.Warning,
                $"Done: {_filesWritten:N0} file(s) written to {_outputRoot} with {_errors} error(s).");

            RunZFightCheck();

            _current = "Done";
            _progress?.Report(new ExtractionProgress(ExtractionPhase.Completed, _bytesDone, _bytesTotal, _filesDone, _filesTotal, _current));

            return new ExtractionResult
            {
                OutputRoot = _outputRoot,
                FixedOutputRoot = _fixedRoot,
                FilesWritten = _filesWritten,
                ErrorCount = _errors,
                DuplicatesSkipped = _duplicates,
                FilesResumed = _filesResumed,
                BytesRead = _bytesDone,
            };
        }

        /// <summary>
        /// Checks the output for duplicate buildings in the same spot. With "Both", the
        /// untouched output is copied first and only the copy is fixed.
        /// </summary>
        private void RunZFightCheck()
        {
            if (_options.ZFight == ZFightMode.Off) return;

            var target = _fixedRoot ?? _outputRoot;
            Directory.CreateDirectory(target);
            if (_fixedRoot is not null)
            {
                Log(LogLevel.Info, $"Copying output to {_fixedRoot} for the z-fixed version.");
                CopyTree(_outputRoot, _fixedRoot);
            }

            _current = "Checking for z-fighting";
            ZFightFixer.Run(target, new ZFightOptions
                {
                    VanillaTextureDictionaries = _options.GameAssets?.TextureDictionaries,
                    KnownNames = _options.GameAssets?.Names,
                },
                _log, _ct,
                (done, total) => _progress?.Report(new ExtractionProgress(ExtractionPhase.CheckingZFight, done, total,
                    done, total, $"Checking ymaps ({done:N0}/{total:N0})")),
                Checkpoint);
        }

        private void CopyTree(string from, string to)
        {
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                Checkpoint();
                var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
        }

        private IEnumerable<string> FindFiles(string pattern)
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            // Skip anything inside the output folders in case they live under the source folder.
            var outputs = new[] { _outputRoot, _fixedRoot }.OfType<string>()
                .Select(p => p.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToList();
            return Directory.EnumerateFiles(_source, pattern, options)
                .Where(p => !outputs.Any(o => p.StartsWith(o, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // DLC patch and update archives go first so their newer files win name clashes in FiveM mode.
        private int ArchivePriority(string path)
        {
            if (!_fiveM) return 0;
            var rel = Relative(path);
            return rel.Contains("patch", StringComparison.OrdinalIgnoreCase)
                || rel.Contains("update", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        private void ProcessArchive(string path)
        {
            var rel = Relative(path);
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                var archive = RpfArchive.Open(fs, Path.GetFileName(path), _options.Keys);
                var dumpDir = Path.Combine(_outputRoot, SafePath.Relative(rel));
                var group = SafePath.Relative(Path.ChangeExtension(rel, null));
                Walk(archive, dumpDir, group, rel.Replace('\\', '/'), Path.GetFileName(path));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (_scanning)
            {
                _failedArchives.Add(path);
                _errors++;
                Log(LogLevel.Error, $"{rel}: {ex.Message}");
            }
            catch (Exception ex)
            {
                _errors++;
                Log(LogLevel.Error, $"{rel}: {ex.Message}");
            }
        }

        /// <param name="innerPath">Path from the top-level archive's file name down to this archive.</param>
        private void Walk(RpfArchive archive, string dumpDir, string group, string display, string innerPath)
        {
            foreach (var file in archive.Files)
            {
                Checkpoint();
                if (file is RpfBinaryEntry { IsArchive: true } nestedEntry)
                {
                    RpfArchive? nested = null;
                    try
                    {
                        nested = archive.OpenNested(nestedEntry);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (!_scanning)
                            Log(LogLevel.Warning, $"{display}/{nestedEntry.Path}: could not open nested archive ({ex.Message})"
                                + (_fiveM ? "; skipped." : "; copied as-is."));
                    }

                    if (nested is not null)
                    {
                        Walk(nested, Path.Combine(dumpDir, nestedEntry.SafeRelativePath), group,
                            $"{display}/{nestedEntry.Path}", $"{innerPath}/{nestedEntry.Path}");
                        continue;
                    }
                    if (_fiveM) continue;
                }

                string dest;
                StagedSupportFile? staged = null;
                if (_fiveM && !FiveMResource.IsMapFile(file.Name))
                {
                    // meta/xml/ini/audio data: staged now, sorted by SupportFileProcessor at the end.
                    if (!SupportFiles.IsCandidate(file.Name)) continue;
                    var inner = $"{innerPath}/{file.Path}";
                    dest = Path.Combine(_outputRoot, SupportFileProcessor.StagingFolder, group, SafePath.Relative(inner));
                    staged = new StagedSupportFile(dest, inner, group);
                }
                else if (_fiveM)
                {
                    if (FiveMResource.IsNonMapFolder($"{innerPath}/{file.Parent?.Path}"))
                    {
                        if (!_scanning) _nonMapSkipped++;
                        continue;
                    }
                    var name = SafePath.Segment(file.Name);
                    if (!Claim(name, $"{display}/{file.Path}")) continue;
                    dest = Path.Combine(_outputRoot, "stream", group, name);
                }
                else
                {
                    dest = Path.Combine(dumpDir, file.SafeRelativePath);
                }

                if (_scanning)
                {
                    _bytesTotal += file.StoredSize;
                    _filesTotal++;
                    continue;
                }

                _current = $"{display}/{file.Path}";
                bool written = WriteFile(dest, file.StoredSize, $"{display}/{file.Path}",
                    (output, onRead) => archive.ExtractTo(file, output, onRead, Checkpoint));
                if (written && staged is not null) _supportFiles.Add(staged);
                if (written && _fiveM && file is RpfResourceEntry res && FiveMResource.IsGen9Resource(res.Name, res.Version))
                    _gen9Count++;
            }
        }

        private void ProcessLoose(string path)
        {
            var name = SafePath.Segment(Path.GetFileName(path));
            var rel = Relative(path).Replace('\\', '/');
            if (!Claim(name, rel)) return;

            long size = new FileInfo(path).Length;
            if (_scanning)
            {
                _bytesTotal += size;
                _filesTotal++;
                return;
            }

            _current = rel;
            WriteFile(Path.Combine(_outputRoot, "stream", LooseGroup, name), size, rel, (output, onRead) =>
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[1 << 20];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    onRead(read);
                    Checkpoint();
                }
            });
        }

        /// <summary>FiveM streams by file name, so each name may only be used once.</summary>
        private bool Claim(string name, string display)
        {
            if (!_fiveM || _claimedNames.Add(name)) return true;
            if (!_scanning)
            {
                _duplicates++;
                if (_duplicates <= MaxDuplicateLogs)
                    Log(LogLevel.Warning, $"Skipped duplicate {name} from {display}.");
            }
            return false;
        }

        /// <returns>True when the file was written successfully.</returns>
        private bool WriteFile(string dest, long inputSize, string display, Action<Stream, Action<long>> extract)
        {
            long reported = 0;
            var key = Path.GetRelativePath(_outputRoot, dest);
            try
            {
                // Finished by an earlier run that crashed or was closed: keep it as-is.
                bool resumed = _journal?.IsCompleted(key) == true && File.Exists(dest);
                if (resumed)
                {
                    _filesResumed++;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    using (var output = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        extract(output, n =>
                        {
                            reported += n;
                            _bytesDone += n;
                            Report();
                        });
                    }
                    _journal?.MarkCompleted(key);
                }

                _filesWritten++;
                if (_fiveM && FiveMResource.IsYtyp(dest))
                    _ytypPaths.Add(key);
                return true;
            }
            catch (OperationCanceledException)
            {
                TryDelete(dest);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(dest);
                _errors++;
                Log(LogLevel.Error, $"{display}: {ex.Message}");
                return false;
            }
            finally
            {
                // Keep the byte count consistent even when a file stops early or fails.
                if (reported < inputSize) _bytesDone += inputSize - reported;
                _filesDone++;
                Report();
            }
        }

        private void WriteManifest(IReadOnlyList<DataFileEntry> dataFiles)
        {
            Directory.CreateDirectory(_outputRoot);
            File.WriteAllText(Path.Combine(_outputRoot, "fxmanifest.lua"), FiveMResource.BuildManifest(_ytypPaths, dataFiles));
            Log(LogLevel.Info, $"Wrote fxmanifest.lua ({_ytypPaths.Count} ytyp file(s), {dataFiles.Count} data file(s) registered).");
        }

        private void Checkpoint() => _pause.WaitIfPaused(_ct);

        private void Report(bool force = false)
        {
            if (_progress is null || (!force && _sinceReport.Elapsed < ReportInterval)) return;
            _sinceReport.Restart();
            var phase = _scanning ? ExtractionPhase.Scanning : ExtractionPhase.Extracting;
            _progress.Report(new ExtractionProgress(phase, _bytesDone, _bytesTotal, _filesDone, _filesTotal, _current));
        }

        private void Log(LogLevel level, string message) => _log?.Invoke(new LogEntry(level, message));

        private string Relative(string path) => Path.GetRelativePath(_source, path);

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
