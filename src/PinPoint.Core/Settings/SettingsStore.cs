using System.Text.Json;
using PinPoint.Core.Diagnostics;

namespace PinPoint.Core.Settings;

/// <summary>Loads/saves <see cref="AppSettings"/> as JSON. Saves are atomic; corrupt files are backed up, not lost.</summary>
public sealed class SettingsStore(string path)
{
    public string FilePath { get; } = path;

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();

            using var stream = File.OpenRead(FilePath);
            var settings = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            return Normalize(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Settings unreadable, using defaults (backup kept as .corrupt)", ex);
            TryBackupCorruptFile();
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        // The deserializer creates a case-sensitive dictionary; restore our comparer.
        settings.Hotkeys = new Dictionary<string, string>(settings.Hotkeys ?? [], StringComparer.OrdinalIgnoreCase);
        settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
        return settings;
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
        }
        catch
        {
            // best effort
        }
    }
}
