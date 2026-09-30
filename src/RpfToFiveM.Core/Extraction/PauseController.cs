namespace RpfToFiveM.Core.Extraction;

/// <summary>Lets the UI pause and resume a running extraction at chunk boundaries.</summary>
public sealed class PauseController
{
    private readonly ManualResetEventSlim _running = new(initialState: true);

    public bool IsPaused => !_running.IsSet;

    public event EventHandler? StateChanged;

    public void Pause()
    {
        _running.Reset();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        _running.Set();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Blocks while paused; throws if cancelled (including while paused).</summary>
    public void WaitIfPaused(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!_running.IsSet) _running.Wait(ct);
    }
}
