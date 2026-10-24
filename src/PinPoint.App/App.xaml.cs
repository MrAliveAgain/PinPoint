using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using PinPoint.Core.Diagnostics;
using PinPoint.Core.Shell;

namespace PinPoint;

public partial class App : Application
{
    private AppHost? _host;

    public App()
    {
        InitializeComponent();

        // Tray app: closing every window must not end the process.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true; // keep pins alive rather than crash mid-game
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Fatal exception", e.ExceptionObject as Exception);
            _host?.RestoreExternalWindows();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Info($"Starting PinPoint {typeof(App).Assembly.GetName().Version}");

        _host = new AppHost(DispatcherQueue.GetForCurrentThread());
        _host.Start();

        // A second launch redirects here: show the window.
        AppInstance.GetCurrent().Activated += (_, _) => _host.Dispatcher.TryEnqueue(() => _host.ShowManager());

        bool background = Environment.GetCommandLineArgs().Contains(StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        if (!background) _host.ShowManager();
    }
}
