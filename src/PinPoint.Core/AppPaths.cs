namespace PinPoint.Core;

/// <summary>Well-known locations. Everything PinPoint writes lives under one of these.</summary>
public static class AppPaths
{
    public const string AppName = "PinPoint";

    /// <summary>%LocalAppData%\PinPoint — settings and logs.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>Pictures\PinPoint — default home for saved snips.</summary>
    public static string DefaultSnipDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);

    public static string SettingsFile { get; } = Path.Combine(DataDirectory, "settings.json");

    /// <summary>%LocalAppData%\PinPoint\notes — one .txt per note.</summary>
    public static string NotesDirectory { get; } = Path.Combine(DataDirectory, "notes");

    /// <summary>%LocalAppData%\PinPoint\boards — one folder per saved board.</summary>
    public static string BoardsDirectory { get; } = Path.Combine(DataDirectory, "boards");
}
