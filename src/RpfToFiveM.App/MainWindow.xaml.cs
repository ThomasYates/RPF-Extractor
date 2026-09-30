using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace RpfToFiveM.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.Log.CollectionChanged += OnLogChanged;
        SourceInitialized += (_, _) => UseDarkTitleBar();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Scroll after layout, otherwise the virtualized list hasn't measured the new item yet.
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_vm.Log.Count > 0) LogList.ScrollIntoView(_vm.Log[^1]);
            });
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_vm.ConfirmClose())
        {
            e.Cancel = true;
            return;
        }
        _vm.SaveSettings();
        base.OnClosing(e);
    }

    private void UseDarkTitleBar()
    {
        const int DwmwaUseImmersiveDarkMode = 20;
        int enabled = 1;
        var hwnd = new WindowInteropHelper(this).Handle;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
