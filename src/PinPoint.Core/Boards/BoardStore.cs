using System.Text.Json;
using System.Text.Json.Serialization;
using PinPoint.Core.Diagnostics;

namespace PinPoint.Core.Boards;

/// <summary>
/// Boards on disk: one folder per board (named by a stable id, so renaming is just a JSON edit)
/// holding <c>board.json</c> and one PNG per snip. Writes are atomic; a broken board.json is skipped
/// in listings rather than taking the others down.
/// </summary>
public sealed class BoardStore(string root)
{
    private const string BoardFile = "board.json";
    private const string ThumbnailFile = "thumbnail.png";

    public string Root { get; } = root;

    public static string CreateId() => $"board-{DateTime.Now:yyyyMMdd-HHmmss-fff}";

    public string FolderOf(string id) => Path.Combine(Root, id);

    public string ImagePath(string id, string snipId) => Path.Combine(FolderOf(id), snipId + ".png");

    /// <summary>Small preview for the Boards page; a cache, redrawn whenever it's older than board.json.</summary>
    public string ThumbnailPath(string id) => Path.Combine(FolderOf(id), ThumbnailFile);

    /// <summary>True if the thumbnail exists and was drawn after the board last changed.</summary>
    public bool IsThumbnailCurrent(string id) =>
        File.Exists(ThumbnailPath(id)) && File.GetLastWriteTimeUtc(ThumbnailPath(id)) >= File.GetLastWriteTimeUtc(Path.Combine(FolderOf(id), BoardFile));

    public void SaveThumbnail(string id, byte[] png)
    {
        string temp = ThumbnailPath(id) + ".tmp";
        File.WriteAllBytes(temp, png);
        File.Move(temp, ThumbnailPath(id), overwrite: true);
    }

    /// <summary>Saved boards, most recently changed first.</summary>
    public IReadOnlyList<BoardInfo> List()
    {
        if (!Directory.Exists(Root)) return [];

        var boards = new List<BoardInfo>();
        foreach (string folder in Directory.EnumerateDirectories(Root))
        {
            string id = Path.GetFileName(folder);
            if (Load(id) is not { } board) continue;
            var modified = File.GetLastWriteTime(Path.Combine(folder, BoardFile));
            boards.Add(new BoardInfo(id, board.Name, modified, board.Snips.Count, board.Notes.Count));
        }

        return [.. boards.OrderByDescending(b => b.Modified)];
    }

    public bool Exists(string id) => File.Exists(Path.Combine(FolderOf(id), BoardFile));

    public Board? Load(string id)
    {
        string path = Path.Combine(FolderOf(id), BoardFile);
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, BoardJsonContext.Default.Board);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Error($"Could not read board {id}", ex);
            return null;
        }
    }

    /// <summary>The board as it would be written (used to tell whether anything changed since the last save).</summary>
    public static string ToJson(Board board) => JsonSerializer.Serialize(board, BoardJsonContext.Default.Board);

    /// <summary>Writes board.json and removes images of snips that are no longer on the board.</summary>
    public void Save(string id, Board board)
    {
        string folder = FolderOf(id);
        Directory.CreateDirectory(folder);
        WriteAtomic(Path.Combine(folder, BoardFile), JsonSerializer.Serialize(board, BoardJsonContext.Default.Board));

        var keep = board.Snips.Select(s => s.Id + ".png").Append(ThumbnailFile).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string image in Directory.EnumerateFiles(folder, "*.png"))
        {
            if (keep.Contains(Path.GetFileName(image))) continue;
            try
            {
                File.Delete(image);
            }
            catch (IOException ex)
            {
                Log.Warn($"Could not remove unused board image {image}: {ex.Message}");
            }
        }
    }

    /// <summary>Stores a snip's image (written once; snip images never change).</summary>
    public void SaveImage(string id, string snipId, byte[] png)
    {
        string path = ImagePath(id, snipId);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(FolderOf(id));
        File.WriteAllBytes(path, png);
    }

    public void Rename(string id, string name)
    {
        if (Load(id) is not { } board) return;
        Save(id, board with { Name = name });
    }

    public void Delete(string id)
    {
        string folder = FolderOf(id);
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static void WriteAtomic(string path, string contents)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Board))]
internal sealed partial class BoardJsonContext : JsonSerializerContext;
