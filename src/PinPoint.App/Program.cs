using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace PinPoint;

/// <summary>
/// Custom entry point: enforce a single instance before any UI exists. A second launch
/// (e.g. double-clicking the exe again) just brings up the running instance's window.
/// </summary>
public static class Program
{
    private const string InstanceKey = "PinPoint.Main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Developer check, no UI: PinPoint.exe --ocr-selftest image.png out.txt
        if (args is ["--ocr-selftest", var image, var output]) return Services.OcrService.RunSelfTest(image, output);

        // Developer check, no UI: PinPoint.exe --board-selftest board-id out.png [thumbnail]
        if (args is ["--board-selftest", var board, var boardImage, .. var rest])
            return Services.BoardService.RunSelfTest(board, boardImage, rest is ["thumbnail"]);

        if (RedirectToRunningInstance()) return 0;

        Application.Start(static _ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        return 0;
    }

    private static bool RedirectToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent) return false;

        // We're the foreground process (the user just launched us); let the running instance take focus.
        PinPoint.Core.Interop.Win32.AllowSetForegroundWindow(main.ProcessId);
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Redirect off the STA thread; blocking here is fine because no UI exists yet.
        Task.Run(() => main.RedirectActivationToAsync(args).AsTask()).Wait();
        return true;
    }
}
