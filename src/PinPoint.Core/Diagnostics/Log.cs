using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PinPoint.Core.Diagnostics;

/// <summary>
/// Tiny thread-safe file logger (%LocalAppData%\PinPoint\logs\pinpoint.log, rolled at 1 MB).
/// Deliberately dependency-free; swap for Microsoft.Extensions.Logging if the app grows.
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly Lock s_gate = new();
    private static string? s_path;

    public static string Directory { get; private set; } = Path.Combine(AppPaths.DataDirectory, "logs");

    /// <summary>Tests redirect logging so they never touch the real log.</summary>
    internal static void RedirectTo(string directory)
    {
        lock (s_gate)
        {
            Directory = directory;
            s_path = null;
        }
    }

    public static void Info(string message, [CallerMemberName] string? caller = null) => Write("INF", message, caller);

    public static void Warn(string message, [CallerMemberName] string? caller = null) => Write("WRN", message, caller);

    public static void Error(string message, Exception? ex = null, [CallerMemberName] string? caller = null) =>
        Write("ERR", ex is null ? message : $"{message}{Environment.NewLine}{ex}", caller);

    private static void Write(string level, string message, string? caller)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {caller}: {message}";
        Debug.WriteLine(line);

        lock (s_gate)
        {
            try
            {
                if (s_path is null)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    s_path = Path.Combine(Directory, "pinpoint.log");
                }

                var info = new FileInfo(s_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(s_path, Path.ChangeExtension(s_path, ".old.log"), overwrite: true);
                }

                File.AppendAllText(s_path, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never take the app down.
            }
        }
    }
}
