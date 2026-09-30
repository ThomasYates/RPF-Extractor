using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RpfToFiveM.App;

public enum StepKey { Prepare, Scan, Extract, Finish, Copy, ZFight }

public enum StepState { Pending, Active, Done, Stopped }

/// <summary>One entry in the step tracker under the progress bar.</summary>
public sealed class StepItem : INotifyPropertyChanged
{
    public StepItem(StepKey key, string name, string title, string hint, bool isFirst)
    {
        Key = key;
        Name = name;
        Title = title;
        Hint = hint;
        IsFirst = isFirst;
    }

    public StepKey Key { get; }

    /// <summary>Short label shown in the tracker.</summary>
    public string Name { get; }

    /// <summary>Heading shown while this step runs.</summary>
    public string Title { get; }

    /// <summary>One-line explanation of what the step is doing.</summary>
    public string Hint { get; }

    public bool IsFirst { get; }

    private StepState _state;
    public StepState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Label));
        }
    }

    public string Label => State == StepState.Done ? "✓ " + Name : Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
