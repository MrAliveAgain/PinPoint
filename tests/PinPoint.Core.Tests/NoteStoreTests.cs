using PinPoint.Core.Notes;

namespace PinPoint.Core.Tests;

public sealed class NoteStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PinPointTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Text_and_layout_round_trip()
    {
        var store = new NoteStore(_dir);
        store.SaveText("a", "first line\nsecond");
        store.SaveLayout("a", new NoteLayout(10, -20, 300, 200, 0.7));
        store.SaveText("b", "no layout yet");

        var notes = store.LoadAll();

        Assert.Equal(["a", "b"], notes.Select(n => n.Id));
        Assert.Equal("first line\nsecond", notes[0].Text);
        Assert.Equal(new NoteLayout(10, -20, 300, 200, 0.7), notes[0].Layout);
        Assert.Null(notes[1].Layout);
        Assert.Equal("no layout yet", File.ReadAllText(Path.Combine(_dir, "b.txt"))); // plain text on disk
    }

    [Fact]
    public void Closing_keeps_notes_with_text_and_deletes_empty_ones()
    {
        var store = new NoteStore(_dir);
        store.SaveText("keep", "important");
        store.SaveLayout("keep", new NoteLayout(0, 0, 100, 100));
        store.SaveText("empty", "   ");

        store.Close("keep");
        store.Close("empty");

        Assert.Empty(store.LoadAll());
        Assert.Equal("important", File.ReadAllText(Path.Combine(store.ClosedDirectory, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(store.ClosedDirectory, "empty.txt")));
    }

    [Fact]
    public void A_broken_layout_file_does_not_lose_notes()
    {
        var store = new NoteStore(_dir);
        store.SaveText("a", "still here");
        File.WriteAllText(Path.Combine(_dir, "layout.json"), "{ broken");

        var note = Assert.Single(store.LoadAll());
        Assert.Equal("still here", note.Text);
        Assert.Null(note.Layout);
    }

    [Fact]
    public void Missing_folder_means_no_notes() => Assert.Empty(new NoteStore(_dir).LoadAll());
}
