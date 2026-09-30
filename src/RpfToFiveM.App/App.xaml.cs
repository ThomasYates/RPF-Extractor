using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace RpfToFiveM.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppLog.Start();
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) AppLog.Exception("Crash", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => AppLog.Exception("Background error", args.Exception);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Stop();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Exception("Unexpected error", e.Exception);
        MessageBox.Show(e.Exception.Message + "\n\nDetails were written to logs.txt.", "RPF Extractor - unexpected error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
