using PinPoint.Core.Boards;
using PinPoint.Core.Drawing;

namespace PinPoint.Core.Tests;

public sealed class BoardStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PinPointTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static Board Sample(string name = "Raid") => new()
    {
        Name = name,
        Snips = [new BoardSnip("s1", new BoardRect(-100, 50, 400, 300), 2, 0.8, [[1, 2, 3, 4], [5, 6]])],
        Notes = [new BoardNote("n1", "line 1\nline 2", new BoardRect(10, 20, 320, 260), 1.0)],
    };

    [Fact]
    public void Board_round_trips_with_snips_strokes_and_notes()
    {
        var store = new BoardStore(_dir);
        store.Save("b", Sample());

        var loaded = store.Load("b")!;

        Assert.Equal("Raid", loaded.Name);
        var snip = Assert.Single(loaded.Snips);
        Assert.Equal(new BoardRect(-100, 50, 400, 300), snip.Bounds);
        Assert.Equal((2, 0.8), (snip.ZoomStep, snip.Opacity));
        Assert.Equal([1.0, 2, 3, 4], snip.Strokes[0]);
        Assert.Equal("line 1\nline 2", Assert.Single(loaded.Notes).Text);
    }

    [Fact]
    public void Listing_is_newest_first_and_skips_broken_boards()
    {
        var store = new BoardStore(_dir);
        store.Save("old", Sample("Old"));
        File.SetLastWriteTime(Path.Combine(store.FolderOf("old"), "board.json"), DateTime.Now.AddDays(-1));
        store.Save("new", Sample("New"));
        Directory.CreateDirectory(store.FolderOf("broken"));
        File.WriteAllText(Path.Combine(store.FolderOf("broken"), "board.json"), "{ nope");

        var list = store.List();

        Assert.Equal(["New", "Old"], list.Select(b => b.Name));
        Assert.Equal((1, 1), (list[0].Snips, list[0].Notes));
    }

    [Fact]
    public void Saving_removes_images_of_snips_no_longer_on_the_board()
    {
        var store = new BoardStore(_dir);
        store.SaveImage("b", "s1", [1, 2, 3]);
        store.SaveImage("b", "gone", [4, 5, 6]);

        store.Save("b", Sample());

        Assert.True(File.Exists(store.ImagePath("b", "s1")));
        Assert.False(File.Exists(store.ImagePath("b", "gone")));
    }

    [Fact]
    public void Rename_and_delete()
    {
        var store = new BoardStore(_dir);
        store.Save("b", Sample("Before"));

        store.Rename("b", "After");
        Assert.Equal("After", store.Load("b")!.Name);

        store.Delete("b");
        Assert.False(store.Exists("b"));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Stroke_history_reset_has_no_undo()
    {
        var history = new StrokeHistory<string>();
        history.Add("x");
        history.Reset(["a", "b"]);

        Assert.Equal(["a", "b"], history.Strokes);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }
}
