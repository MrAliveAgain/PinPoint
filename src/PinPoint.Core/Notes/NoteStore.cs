using System.Text.Json;
using System.Text.Json.Serialization;
using PinPoint.Core.Diagnostics;

namespace PinPoint.Core.Notes;

/// <summary>
/// Where a note window sits (physical pixels), how faded it is, and the .txt file the user saved it to
/// with Save/Save as (null until then; Ctrl+S then saves straight back to it, like Notepad).
/// </summary>
public sealed record NoteLayout(int X, int Y, int Width, int Height, double Opacity = 1.0, string? ExportPath = null);

public sealed record StoredNote(string Id, string Text, NoteLayout? Layout);

/// <summary>
/// Notes live as plain <c>.txt</c> files (easy to find, back up or edit elsewhere) plus one
/// <c>layout.json</c> remembering each note's position, size and opacity. Closing a note that has
/// text moves its file to <c>closed\</c> instead of deleting it, so nothing is lost by a misclick.
/// </summary>
public sealed class NoteStore(string directory)
{
    private const string LayoutFile = "layout.json";

    public string Directory { get; } = directory;

    public string ClosedDirectory => Path.Combine(Directory, "closed");

    public static string CreateId() => $"Note {DateTime.Now:yyyy-MM-dd HH-mm-ss-fff}";

    public IReadOnlyList<StoredNote> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];

        var layouts = LoadLayouts();
        var notes = new List<StoredNote>();
        foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.txt").Order(StringComparer.Ordinal))
        {
            try
            {
                string id = Path.GetFileNameWithoutExtension(file);
                notes.Add(new StoredNote(id, File.ReadAllText(file), layouts.GetValueOrDefault(id)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Could not read note {file}", ex);
            }
        }

        return notes;
    }

    public void SaveText(string id, string text) => WriteAtomic(TextPath(id), text);

    public void SaveLayout(string id, NoteLayout layout)
    {
        var layouts = LoadLayouts();
        layouts[id] = layout;
        WriteLayouts(layouts);
    }

    /// <summary>Removes a note. Notes with text are kept in <see cref="ClosedDirectory"/> rather than deleted.</summary>
    public void Close(string id)
    {
        try
        {
            string path = TextPath(id);
            if (File.Exists(path))
            {
                if (string.IsNullOrWhiteSpace(File.ReadAllText(path)))
                {
                    File.Delete(path);
                }
                else
                {
                    System.IO.Directory.CreateDirectory(ClosedDirectory);
                    File.Move(path, Path.Combine(ClosedDirectory, Path.GetFileName(path)), overwrite: true);
                }
            }

            var layouts = LoadLayouts();
            if (layouts.Remove(id)) WriteLayouts(layouts);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not close note {id}", ex);
        }
    }

    private string TextPath(string id) => Path.Combine(Directory, id + ".txt");

    private Dictionary<string, NoteLayout> LoadLayouts()
    {
        string path = Path.Combine(Directory, LayoutFile);
        try
        {
            if (!File.Exists(path)) return [];
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, NoteJsonContext.Default.DictionaryStringNoteLayout) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Positions are a nicety; a broken layout file just means notes open at default spots.
            Log.Warn($"Ignoring unreadable note layout: {ex.Message}");
            return [];
        }
    }

    private void WriteLayouts(Dictionary<string, NoteLayout> layouts) =>
        WriteAtomic(Path.Combine(Directory, LayoutFile), JsonSerializer.Serialize(layouts, NoteJsonContext.Default.DictionaryStringNoteLayout));

    private void WriteAtomic(string path, string contents)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not save {path}", ex);
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, NoteLayout>))]
internal sealed partial class NoteJsonContext : JsonSerializerContext;
