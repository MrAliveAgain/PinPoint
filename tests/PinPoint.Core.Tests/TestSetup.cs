using System.Runtime.CompilerServices;
using PinPoint.Core.Diagnostics;

namespace PinPoint.Core.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Log.RedirectTo(Path.Combine(Path.GetTempPath(), "PinPointTests", "logs"));
}
