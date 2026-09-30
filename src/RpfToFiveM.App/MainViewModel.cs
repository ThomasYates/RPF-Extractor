using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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
            using var journal = _jobs.OpenJournal();
            if (resume) AddLog(LogLevel.Info, $"Resuming: {journal.CompletedCount:N0} file(s) already done will be skipped.");

            StatusText = "Loading keys";
            var keys = await LoadKeysAsync();
            var gameAssets = ZFight == ZFightMode.Off ? null : await LoadGameAssetsAsync(keys, _cts.Token);

            StatusText = "Scanning";
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
            };
            var progress = new Progress<ExtractionProgress>(OnProgress);
            var result = await new RpfExtractor().RunAsync(options, progress, OnLog, _pause, _cts.Token);

            finished = true;
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
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped";
            AddLog(LogLevel.Warning, "Stopped. Progress is saved — use Resume to continue later.");
            TaskbarState = TaskbarItemProgressState.None;
        }
        catch (Exception ex)
        {
            StatusText = "Failed";
            AddLog(LogLevel.Error, ex.Message);
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
            AddLog(LogLevel.Info, "Z-fight check without a GTA V folder: only exact duplicates and models with no textures at all can be fixed.");
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

    private void OnProgress(ExtractionProgress p)
    {
        ElapsedText = FormatDuration(_activeTime.Elapsed);
        CurrentItem = p.CurrentItem;

        if (p.Phase == ExtractionPhase.Scanning)
        {
            IsIndeterminate = true;
            TaskbarState = IsPaused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Indeterminate;
            StatusText = IsPaused ? "Paused" : "Scanning";
            return;
        }

        if (p.Phase == ExtractionPhase.CheckingZFight)
        {
            IsIndeterminate = false;
            ProgressValue = p.Fraction;
            PercentText = $"{p.Fraction * 100:0.0}";
            FilesText = $"{p.FilesDone:N0} / {p.FilesTotal:N0} ymaps";
            SpeedText = EtaText = "—";
            if (!IsPaused) StatusText = "Checking for z-fighting";
            return;
        }

        // Remember the total so the resume prompt can show "x of y".
        if (_currentJob is not null && _currentJob.FilesTotal != p.FilesTotal)
        {
            _currentJob.FilesTotal = p.FilesTotal;
            _jobs.Save(_currentJob);
        }

        IsIndeterminate = false;
        ProgressValue = p.Fraction;
        PercentText = $"{p.Fraction * 100:0.0}";
        FilesText = $"{p.FilesDone:N0} / {p.FilesTotal:N0}";
        SizeText = $"{FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}";
        if (!IsPaused)
        {
            StatusText = p.Phase == ExtractionPhase.Completed ? "Finishing" : "Extracting";
            TaskbarState = TaskbarItemProgressState.Normal;
        }
        UpdateSpeed(p);
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
            StatusText = IsIndeterminate ? "Scanning" : "Extracting";
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

    private void ResetStats()
    {
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
