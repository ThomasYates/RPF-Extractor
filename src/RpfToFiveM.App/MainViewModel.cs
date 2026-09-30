using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;
using Microsoft.Win32;
using RpfToFiveM.Core.Crypto;
using RpfToFiveM.Core.Extraction;
using RpfToFiveM.Core.Maps;

namespace RpfToFiveM.App;

public sealed record LogItem(string Time, LogLevel Level, string Message)
{
    public string Tag => Level switch { LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "INF" };
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int MaxLogItems = 5000;
    private const string AppName = "RPF Extractor";

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly JobStore _jobs = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RpfToFiveM", "job"));
    private readonly PauseController _pause = new();
    private readonly Stopwatch _activeTime = new();
    private readonly Queue<(double Seconds, long Bytes)> _speedSamples = new();
    private CancellationTokenSource? _cts;
    private GtaKeys? _keys;
    private string? _keysLoadedFor;
    private JobInfo? _currentJob;

    public MainViewModel()
    {
        _sourceFolder = _settings.SourceFolder;
        _outputFolder = _settings.OutputFolder;
        _gameFolder = string.IsNullOrWhiteSpace(_settings.GameFolder)
            ? GameLocator.FindGameFolders().FirstOrDefault() ?? ""
            : _settings.GameFolder;
        _fiveMMode = _settings.FiveMMode;
        _resourceName = _settings.ResourceName;
        _zFight = _settings.ZFight;

        BrowseSourceCommand = new RelayCommand(() => SourceFolder = PickFolder("Select the folder containing RPF files", SourceFolder) ?? SourceFolder, () => !IsRunning);
        BrowseOutputCommand = new RelayCommand(() => OutputFolder = PickFolder("Select where to export", OutputFolder) ?? OutputFolder, () => !IsRunning);
        BrowseGameCommand = new RelayCommand(() => GameFolder = PickFolder("Select your GTA V install folder", GameFolder) ?? GameFolder, () => !IsRunning);
        StartCommand = new RelayCommand(async () => await StartAsync(resume: false), () => !IsRunning);
        ResumeJobCommand = new RelayCommand(async () => await StartAsync(resume: true), () => !IsRunning && HasPendingJob);
        DiscardJobCommand = new RelayCommand(DiscardJob, () => !IsRunning && HasPendingJob);
        PauseResumeCommand = new RelayCommand(TogglePause, () => IsRunning);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        OpenOutputCommand = new RelayCommand(OpenOutput, () => Directory.Exists(LastOutput ?? OutputFolder));
        ClearLogCommand = new RelayCommand(() => Log.Clear());
        OpenLogFileCommand = new RelayCommand(OpenLogFile, () => File.Exists(AppLog.FilePath));
        CopyPoolLinesCommand = new RelayCommand(CopyPoolLines, () => HasPoolLines);

        AppLog.SetFolders(() => new[]
        {
            (SourceFolder, "<source>"),
            (OutputFolder, "<export>"),
            (GameFolder, "<gta>"),
        });

        RefreshPendingJob();
    }

    public ObservableCollection<LogItem> Log { get; } = new();

    public ICommand BrowseSourceCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand BrowseGameCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand ResumeJobCommand { get; }
    public ICommand DiscardJobCommand { get; }
    public ICommand PauseResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand OpenLogFileCommand { get; }
    public ICommand CopyPoolLinesCommand { get; }

    #region Bindable properties

    private string _sourceFolder;
    public string SourceFolder { get => _sourceFolder; set => Set(ref _sourceFolder, value); }

    private string _outputFolder;
    public string OutputFolder { get => _outputFolder; set => Set(ref _outputFolder, value); }

    private string _gameFolder;
    public string GameFolder { get => _gameFolder; set => Set(ref _gameFolder, value); }

    private bool _fiveMMode;
    public bool FiveMMode
    {
        get => _fiveMMode;
        set
        {
            if (!Set(ref _fiveMMode, value)) return;
            OnPropertyChanged(nameof(DumpMode));
            OnPropertyChanged(nameof(ModeDescription));
            OnPropertyChanged(nameof(ZFightDescription));
        }
    }

    /// <summary>Inverse of <see cref="FiveMMode"/> for the two-way mode selector.</summary>
    public bool DumpMode
    {
        get => !FiveMMode;
        set { if (value) FiveMMode = false; }
    }

    private string _resourceName;
    public string ResourceName
    {
        get => _resourceName;
        set { if (Set(ref _resourceName, value)) OnPropertyChanged(nameof(ZFightDescription)); }
    }

    private ZFightMode _zFight;
    public ZFightMode ZFight
    {
        get => _zFight;
        set
        {
            if (!Set(ref _zFight, value)) return;
            OnPropertyChanged(nameof(ZFightOff));
            OnPropertyChanged(nameof(ZFightOn));
            OnPropertyChanged(nameof(ZFightBoth));
            OnPropertyChanged(nameof(ZFightDescription));
        }
    }

    // One bool per option for the three-way selector.
    public bool ZFightOff { get => ZFight == ZFightMode.Off; set { if (value) ZFight = ZFightMode.Off; } }
    public bool ZFightOn { get => ZFight == ZFightMode.On; set { if (value) ZFight = ZFightMode.On; } }
    public bool ZFightBoth { get => ZFight == ZFightMode.Both; set { if (value) ZFight = ZFightMode.Both; } }

    public string ZFightDescription
    {
        get
        {
            var res = FiveMResource.SanitizeResourceName(ResourceName);
            return ZFight switch
            {
                ZFightMode.On => "Deletes exact duplicate buildings, and untextured grey copies sitting on a textured version. Writes zfight-report.txt.",
                ZFightMode.Both => FiveMMode
                    ? $"Writes two resources to compare: {res} (untouched) and {res}_zfix (fixed)."
                    : "Writes two folders to compare: \"extracted\" (untouched) and \"extracted (ZFightFix)\" (fixed).",
                _ => "Output is left exactly as extracted.",
            };
        }
    }

    public string ModeDescription => FiveMMode
        ? "Writes fxmanifest.lua and a stream folder with ymap, ytyp, ydr, ydd, yft, ytd, ybn, ynv, ynd and ycd files. Useful meta/xml/audio data is registered automatically; the rest goes to _not_used (see meta-report.txt)."
        : "Creates an \"extracted\" folder in the export location. Every archive, nested ones included, becomes a plain folder inside it with its original layout.";

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set { if (Set(ref _isRunning, value)) OnPropertyChanged(nameof(IsIdle)); }
    }
    public bool IsIdle => !IsRunning;

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set { if (Set(ref _isPaused, value)) OnPropertyChanged(nameof(PauseButtonText)); }
    }
    public string PauseButtonText => IsPaused ? "Resume" : "Pause";

    private bool _hasPendingJob;
    public bool HasPendingJob { get => _hasPendingJob; private set => Set(ref _hasPendingJob, value); }

    private string _pendingJobText = "";
    public string PendingJobText { get => _pendingJobText; private set => Set(ref _pendingJobText, value); }

    private double _progressValue;
    public double ProgressValue { get => _progressValue; private set => Set(ref _progressValue, value); }

    private bool _isIndeterminate;
    public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }

    private TaskbarItemProgressState _taskbarState = TaskbarItemProgressState.None;
    public TaskbarItemProgressState TaskbarState { get => _taskbarState; private set => Set(ref _taskbarState, value); }

    private string _statusText = "Idle";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Steps for the running job, shown under the progress bar.</summary>
    public ObservableCollection<StepItem> Steps { get; } = new();
    private StepKey? _currentStep;
    private string _stepTitle = "";

    private bool _hasPoolAdvice;
    public bool HasPoolAdvice { get => _hasPoolAdvice; private set => Set(ref _hasPoolAdvice, value); }

    private string _poolLines = "";
    public string PoolLines
    {
        get => _poolLines;
        private set { if (Set(ref _poolLines, value)) OnPropertyChanged(nameof(HasPoolLines)); }
    }
    public bool HasPoolLines => PoolLines.Length > 0;

    private string _poolNote = "";
    public string PoolNote { get => _poolNote; private set => Set(ref _poolNote, value); }

    private string _stepText = "";
    public string StepText { get => _stepText; private set => Set(ref _stepText, value); }

    private string _percentText = "0.0";
    public string PercentText { get => _percentText; private set => Set(ref _percentText, value); }

    private string _filesText = "—";
    public string FilesText { get => _filesText; private set => Set(ref _filesText, value); }

    private string _sizeText = "—";
    public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }

    private string _speedText = "—";
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }

    private string _etaText = "—";
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }

    private string _elapsedText = "—";
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    private string _currentItem = "";
    public string CurrentItem { get => _currentItem; private set => Set(ref _currentItem, value); }

    private string _keysText = "Keys not loaded";
    public string KeysText { get => _keysText; private set => Set(ref _keysText, value); }

    private string? _lastOutput;
    public string? LastOutput { get => _lastOutput; private set => Set(ref _lastOutput, value); }

    #endregion

    private async Task StartAsync(bool resume)
    {
        if (resume)
        {
            var pending = _jobs.Load();
            if (pending is null) { RefreshPendingJob(); return; }
            SourceFolder = pending.SourceFolder;
            OutputFolder = pending.OutputFolder;
            GameFolder = pending.GameFolder;
            FiveMMode = pending.Mode == OutputMode.FiveM;
            ResourceName = pending.ResourceName;
            ZFight = pending.ZFight;
            _currentJob = pending;
        }

        if (!Validate()) return;
        SaveSettings();

        if (!resume)
        {
            _jobs.Clear();
            _currentJob = new JobInfo
            {
                SourceFolder = SourceFolder,
                OutputFolder = OutputFolder,
                GameFolder = GameFolder,
                Mode = FiveMMode ? OutputMode.FiveM : OutputMode.Dump,
                ResourceName = ResourceName,
                ZFight = ZFight,
            };
        }
        _jobs.Save(_currentJob!);
        HasPendingJob = false;

        IsRunning = true;
        IsPaused = false;
        _pause.Resume();
        _cts = new CancellationTokenSource();
        ResetStats();
        TaskbarState = TaskbarItemProgressState.Indeterminate;
        IsIndeterminate = true;
        bool finished = false;

        try
        {
            AppLog.Write(LogLevel.Info,
                $"Job {(resume ? "resumed" : "started")}: {(FiveMMode ? $"FiveM map '{FiveMResource.SanitizeResourceName(ResourceName)}'" : "folder dump")}, " +
                $"z-fight {ZFight}, source {SourceFolder}, export {OutputFolder}, GTA V folder {(string.IsNullOrWhiteSpace(GameFolder) ? "not set" : GameFolder)}");

            using var journal = _jobs.OpenJournal();
            if (resume) AddLog(LogLevel.Info, $"Resuming: {journal.CompletedCount:N0} file(s) already done will be skipped.");

            BuildSteps();
            EnterStep(StepKey.Prepare);
            var keys = await LoadKeysAsync();
            // The pool check (FiveM mode) also uses the base-game index, for how full each pool already is.
            var gameAssets = ZFight == ZFightMode.Off && !FiveMMode ? null : await LoadGameAssetsAsync(keys, _cts.Token);
            var poolLimits = FiveMMode ? await FetchPoolLimitsAsync(_cts.Token) : null;

            EnterStep(StepKey.Scan);
            _activeTime.Restart();
            var options = new ExtractionOptions
            {
                SourceFolder = SourceFolder,
                OutputFolder = OutputFolder,
                Mode = FiveMMode ? OutputMode.FiveM : OutputMode.Dump,
                ResourceName = ResourceName,
                Keys = keys,
                Journal = journal,
                ZFight = ZFight,
                GameAssets = gameAssets,
                PoolLimits = poolLimits,
            };
            var progress = new Progress<ExtractionProgress>(OnProgress);
            var result = await new RpfExtractor().RunAsync(options, progress, OnLog, _pause, _cts.Token);

            finished = true;
            ShowPoolAdvice(result.Pools);
            // With two outputs, open their shared parent so both are visible.
            LastOutput = result.FixedOutputRoot is null ? result.OutputRoot : Path.GetDirectoryName(result.OutputRoot);
            if (result.FixedOutputRoot is not null)
                AddLog(LogLevel.Info, $"Untouched: {result.OutputRoot}  ·  Fixed: {result.FixedOutputRoot}");
            StatusText = result.ErrorCount == 0
                ? $"Done · {result.FilesWritten:N0} files"
                : $"Done · {result.FilesWritten:N0} files · {result.ErrorCount} error(s)";
            ProgressValue = 1;
            PercentText = "100.0";
            EtaText = "—";
            TaskbarState = result.ErrorCount == 0 ? TaskbarItemProgressState.None : TaskbarItemProgressState.Error;
            FinishSteps(StepState.Done);
            StepText = result.ErrorCount == 0 ? "All steps finished." : "Finished with errors. See the log below for details.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped";
            FinishSteps(StepState.Stopped);
            StepText = "Stopped. Progress is saved; use Resume job to carry on later.";
            AddLog(LogLevel.Warning, "Stopped. Progress is saved — use Resume to continue later.");
            TaskbarState = TaskbarItemProgressState.None;
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            FinishSteps(StepState.Stopped);
            StepText = "Failed. See the log below; progress is saved.";
            AddLog(LogLevel.Error, ex.Message);
            AppLog.Exception("Job failed", ex);
            AddLog(LogLevel.Info, "Progress is saved — fix the problem and use Resume to continue.");
            TaskbarState = TaskbarItemProgressState.Error;
        }
        finally
        {
            // A finished job needs no recovery; anything else stays resumable.
            if (finished) _jobs.Clear();
            _activeTime.Stop();
            IsIndeterminate = false;
            IsRunning = false;
            IsPaused = false;
            _pause.Resume();
            _cts?.Dispose();
            _cts = null;
            CurrentItem = "";
            RefreshPendingJob();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void RefreshPendingJob()
    {
        var job = _jobs.Load();
        HasPendingJob = job is not null;
        if (job is null) return;

        int done = _jobs.CompletedCount();
        string count = job.FilesTotal > 0 ? $"{done:N0} of {job.FilesTotal:N0} files" : $"{done:N0} files";
        string mode = job.Mode == OutputMode.FiveM ? $"FiveM map \"{job.ResourceName}\"" : "folder dump";
        if (job.ZFight != ZFightMode.Off) mode += $" (z-fix {job.ZFight.ToString().ToLowerInvariant()})";
        PendingJobText = $"Unfinished {mode} from {job.StartedUtc.ToLocalTime():d MMM, HH:mm} — {count} done. " +
                         $"{Path.GetFileName(job.SourceFolder.TrimEnd('\\'))} → {job.OutputFolder}";
    }

    private void DiscardJob()
    {
        _jobs.Clear();
        RefreshPendingJob();
        AddLog(LogLevel.Info, "Discarded the unfinished job. Files already written were left in place.");
    }

    private bool Validate()
    {
        string? error = null;
        if (string.IsNullOrWhiteSpace(SourceFolder) || !Directory.Exists(SourceFolder))
            error = "Choose an existing RPF source folder.";
        else if (string.IsNullOrWhiteSpace(OutputFolder))
            error = "Choose an export location.";
        else if (string.Equals(Path.GetFullPath(SourceFolder).TrimEnd('\\'), Path.GetFullPath(OutputFolder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            error = "The export location must be different from the source folder.";

        if (error is null) return true;
        MessageBox.Show(Application.Current.MainWindow!, error, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task<GtaKeys?> LoadKeysAsync()
    {
        var folder = GameFolder?.Trim() ?? "";
        if (folder.Length == 0)
        {
            KeysText = "No GTA V folder · unencrypted archives only";
            AddLog(LogLevel.Info, "No GTA V folder set — only unencrypted (OpenIV/mod) archives can be read.");
            return null;
        }
        if (_keys is not null && string.Equals(_keysLoadedFor, folder, StringComparison.OrdinalIgnoreCase))
            return _keys;

        try
        {
            if (TryCachedKey(out var cached))
            {
                _keys = await Task.Run(() => GtaKeys.FromAesKey(cached));
            }
            else
            {
                AddLog(LogLevel.Info, "Reading decryption keys from the game executable (first run only).");
                _keys = await Task.Run(() => GtaKeys.LoadFromGameFolder(folder));
                _settings.CachedAesKey = Convert.ToBase64String(_keys.AesKey);
                _settings.Save();
            }
            _keysLoadedFor = folder;
            KeysText = $"Keys loaded · {Path.GetFileName(GameLocator.FindExecutable(folder) ?? "cache")}";
            AddLog(LogLevel.Info, "Decryption keys loaded.");
            return _keys;
        }
        catch (Exception ex)
        {
            KeysText = "Keys unavailable";
            AddLog(LogLevel.Warning, $"Could not load keys from the GTA V folder ({ex.Message}). Encrypted archives will be skipped.");
            return null;
        }
    }

    /// <summary>
    /// Base-game texture and model names for the z-fight check, built from the game's archive
    /// tables once and cached. Without it, models that may use vanilla textures are never
    /// treated as grey.
    /// </summary>
    private async Task<GameAssetIndex?> LoadGameAssetsAsync(GtaKeys? keys, CancellationToken ct)
    {
        var folder = GameFolder?.Trim() ?? "";
        if (folder.Length == 0 || keys is null)
        {
            AddLog(LogLevel.Info, "No GTA V folder set: the z-fight check only fixes exact duplicates and models with no textures at all, and pool sizes use built-in base game counts.");
            return null;
        }
        try
        {
            StatusText = "Indexing base game";
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RpfToFiveM", "game-assets.json");
            var index = await Task.Run(() => GameAssetIndex.LoadOrBuild(folder, keys, cache, ct), ct);
            AddLog(LogLevel.Info, $"Base game index: {index.TextureDictionaries.Count:N0} texture dictionaries, {index.Names.Count:N0} models.");
            return index;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AddLog(LogLevel.Warning, $"Couldn't index the base game ({ex.Message}); the z-fight check will be more cautious.");
            return null;
        }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>FiveM's live list of pools servers may raise, and by how much. Falls back to a built-in copy offline.</summary>
    private async Task<IReadOnlyDictionary<string, int>?> FetchPoolLimitsAsync(CancellationToken ct)
    {
        var limits = await PoolAdvisor.FetchLimitsAsync(Http, ct);
        if (limits is null) AddLog(LogLevel.Info, "Couldn't fetch FiveM's pool limits (offline?); using the built-in list.");
        return limits;
    }

    private void ShowPoolAdvice(PoolReport? report)
    {
        PoolLines = report?.ServerCfgLines ?? "";
        HasPoolAdvice = report is not null && (report.HasRecommendations || report.HasWarnings);
        if (!HasPoolAdvice) { PoolNote = ""; return; }

        var parts = new List<string>();
        var raise = report!.Pools.Where(p => p.Status is PoolStatus.Increase or PoolStatus.CannotFit).ToList();
        if (raise.Count > 0)
            parts.Add($"This map would overflow or nearly fill {string.Join(", ", raise.Select(p => p.Pool))}. " +
                      "Paste these lines into server.cfg (before your resources start), then restart the server.");
        foreach (var p in report.Pools.Where(p => p.Status is PoolStatus.NotRaisable or PoolStatus.CannotFit))
            parts.Add($"{p.Pool}: {p.Advice}");
        parts.Add("Full breakdown in pool-sizes.txt in the resource folder.");
        PoolNote = string.Join(Environment.NewLine, parts);
    }

    private void CopyPoolLines()
    {
        try
        {
            Clipboard.SetText(PoolLines);
            AddLog(LogLevel.Info, "server.cfg lines copied to the clipboard.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            AddLog(LogLevel.Warning, "Couldn't reach the clipboard; select the lines and press Ctrl+C instead.");
        }
    }

    private bool TryCachedKey(out byte[] key)
    {
        key = Array.Empty<byte>();
        try
        {
            if (string.IsNullOrEmpty(_settings.CachedAesKey)) return false;
            key = Convert.FromBase64String(_settings.CachedAesKey);
            return GtaKeys.IsValidAesKey(key);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>The steps this job will go through, based on the chosen format and z-fight mode.</summary>
    private void BuildSteps()
    {
        var res = FiveMResource.SanitizeResourceName(ResourceName);
        var list = new List<(StepKey Key, string Name, string Title, string Hint)>
        {
            (StepKey.Prepare, "Prepare", "Getting ready",
                "Loading decryption keys and, for the z-fight check, indexing the base game (slow only the first time)."),
            (StepKey.Scan, "Scan", "Scanning archives",
                "Reading every archive's table of contents to count what needs extracting. Nothing is written yet."),
            (StepKey.Extract, "Extract", "Extracting",
                "Unpacking files from the archives into the export folder."),
        };
        if (FiveMMode)
            list.Add((StepKey.Finish, "Finish", "Finishing up",
                "Writing fxmanifest.lua and sorting meta files into data/ and _not_used/."));
        if (ZFight == ZFightMode.Both)
            list.Add((StepKey.Copy, "Copy for z-fix", "Copying for the z-fix version",
                FiveMMode
                    ? $"Copying the finished map to {res}_zfix, so {res} stays untouched and the copy gets fixed."
                    : "Copying the finished dump to \"extracted (ZFightFix)\", so the original stays untouched and the copy gets fixed."));
        if (ZFight != ZFightMode.Off)
            list.Add((StepKey.ZFight, "Z-fight check", "Checking for z-fighting",
                "Finding duplicate or grey copies of buildings in the same spot and removing them. Details go in zfight-report.txt."));

        Steps.Clear();
        for (int i = 0; i < list.Count; i++)
            Steps.Add(new StepItem(list[i].Key, list[i].Name, list[i].Title, list[i].Hint, isFirst: i == 0));
        _currentStep = null;
    }

    /// <summary>Marks earlier steps done and this one active, and resets the per-step counters.</summary>
    private void EnterStep(StepKey key)
    {
        if (_currentStep == key) return;
        _currentStep = key;
        int index = Steps.ToList().FindIndex(s => s.Key == key);
        for (int i = 0; i < Steps.Count; i++)
            Steps[i].State = i < index ? StepState.Done : i == index ? StepState.Active : StepState.Pending;

        var step = index >= 0 ? Steps[index] : null;
        _stepTitle = step?.Title ?? "";
        StepText = step is null ? "" : $"Step {index + 1} of {Steps.Count}: {step.Hint}";
        if (!IsPaused) StatusText = _stepTitle;

        // Each step has its own progress and rate.
        _speedSamples.Clear();
        ProgressValue = 0;
        PercentText = "0.0";
        SpeedText = EtaText = "—";
    }

    private void FinishSteps(StepState last)
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            if (last == StepState.Done) Steps[i].State = StepState.Done;
            else if (Steps[i].State == StepState.Active) Steps[i].State = last;
        }
        _currentStep = null;
    }

    private void OnProgress(ExtractionProgress p)
    {
        ElapsedText = FormatDuration(_activeTime.Elapsed);
        CurrentItem = p.CurrentItem;

        StepKey? step = p.Phase switch
        {
            ExtractionPhase.Scanning => StepKey.Scan,
            ExtractionPhase.Extracting => StepKey.Extract,
            ExtractionPhase.Finishing => StepKey.Finish,
            ExtractionPhase.CopyingForZFix => StepKey.Copy,
            ExtractionPhase.CheckingZFight => StepKey.ZFight,
            _ => null,
        };
        if (step is null) return; // Completed is handled when the job returns
        EnterStep(step.Value);

        bool indeterminate = p.Phase is ExtractionPhase.Scanning or ExtractionPhase.Finishing
                             || (p.Phase == ExtractionPhase.CheckingZFight && p.FilesTotal == 0);
        IsIndeterminate = indeterminate;
        TaskbarState = IsPaused ? TaskbarItemProgressState.Paused
            : indeterminate ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal;
        if (indeterminate) return;

        ProgressValue = p.Fraction;
        PercentText = $"{p.Fraction * 100:0.0}";

        switch (p.Phase)
        {
            case ExtractionPhase.CheckingZFight:
                FilesText = $"{p.FilesDone:N0} / {p.FilesTotal:N0} ymaps";
                SizeText = SpeedText = EtaText = "—";
                break;

            case ExtractionPhase.CopyingForZFix:
                FilesText = $"{p.FilesDone:N0} / {p.FilesTotal:N0}";
                SizeText = $"{FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}";
                UpdateSpeed(p);
                break;

            default:
                // Remember the total so the resume prompt can show "x of y".
                if (_currentJob is not null && _currentJob.FilesTotal != p.FilesTotal)
                {
                    _currentJob.FilesTotal = p.FilesTotal;
                    _jobs.Save(_currentJob);
                }
                FilesText = $"{p.FilesDone:N0} / {p.FilesTotal:N0}";
                SizeText = $"{FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}";
                UpdateSpeed(p);
                break;
        }
    }

    private void UpdateSpeed(ExtractionProgress p)
    {
        // Rate over a sliding window of active (unpaused) time.
        double now = _activeTime.Elapsed.TotalSeconds;
        _speedSamples.Enqueue((now, p.BytesDone));
        while (_speedSamples.Count > 2 && now - _speedSamples.Peek().Seconds > 4) _speedSamples.Dequeue();

        var first = _speedSamples.Peek();
        double window = now - first.Seconds;
        if (window < 0.5) return;

        double speed = (p.BytesDone - first.Bytes) / window;
        SpeedText = $"{FormatBytes((long)speed)}/s";
        EtaText = speed > 0 ? FormatDuration(TimeSpan.FromSeconds((p.BytesTotal - p.BytesDone) / speed)) : "—";
    }

    private void OnLog(LogEntry entry) =>
        Application.Current.Dispatcher.BeginInvoke(() => AddLog(entry.Level, entry.Message, entry.Time));

    private void AddLog(LogLevel level, string message, DateTime? time = null)
    {
        AppLog.Write(level, message);
        Log.Add(new LogItem((time ?? DateTime.Now).ToString("HH:mm:ss"), level, message));
        while (Log.Count > MaxLogItems) Log.RemoveAt(0);
    }

    private void TogglePause()
    {
        if (_pause.IsPaused)
        {
            _pause.Resume();
            _activeTime.Start();
            _speedSamples.Clear();
            IsPaused = false;
            StatusText = _stepTitle;
            TaskbarState = IsIndeterminate ? TaskbarItemProgressState.Indeterminate : TaskbarItemProgressState.Normal;
            AddLog(LogLevel.Info, "Resumed.");
        }
        else
        {
            _pause.Pause();
            _activeTime.Stop();
            IsPaused = true;
            StatusText = "Paused";
            SpeedText = "—";
            TaskbarState = TaskbarItemProgressState.Paused;
            AddLog(LogLevel.Info, "Paused.");
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Stopping";
    }

    private void OpenOutput()
    {
        var path = LastOutput ?? OutputFolder;
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void OpenLogFile()
    {
        if (File.Exists(AppLog.FilePath))
            Process.Start(new ProcessStartInfo(AppLog.FilePath) { UseShellExecute = true });
    }

    private void ResetStats()
    {
        ShowPoolAdvice(null);
        ProgressValue = 0;
        PercentText = "0.0";
        FilesText = SizeText = SpeedText = EtaText = ElapsedText = "—";
        CurrentItem = "";
        LastOutput = null;
        _speedSamples.Clear();
    }

    public void SaveSettings()
    {
        _settings.SourceFolder = SourceFolder;
        _settings.OutputFolder = OutputFolder;
        _settings.GameFolder = GameFolder;
        _settings.FiveMMode = FiveMMode;
        _settings.ResourceName = ResourceName;
        _settings.ZFight = ZFight;
        _settings.Save();
    }

    /// <summary>Stops any running job; returns false if the user wants to keep it running.</summary>
    public bool ConfirmClose()
    {
        if (!IsRunning) return true;
        var answer = MessageBox.Show(Application.Current.MainWindow!,
            "An extraction is still running. Stop it and exit?\n\nProgress is saved and you can resume next time.",
            AppName, MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return false;
        _cts?.Cancel();
        _pause.Resume();
        return true;
    }

    private static string? PickFolder(string title, string current)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{v:0} {units[u]}" : $"{v:0.0} {units[u]}";
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
