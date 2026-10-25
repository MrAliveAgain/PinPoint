using PinPoint.Core.Drawing;

namespace PinPoint.Core.Tests;

public class StrokeHistoryTests
{
    [Fact]
    public void Undo_and_redo_walk_back_and_forth()
    {
        var history = new StrokeHistory<string>();
        history.Add("a");
        history.Add("b");

        history.Undo();
        Assert.Equal(["a"], history.Strokes);
        Assert.True(history.CanRedo);

        history.Redo();
        Assert.Equal(["a", "b"], history.Strokes);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Clear_is_undoable_and_redoable()
    {
        var history = new StrokeHistory<string>();
        history.Add("a");
        history.Add("b");

        history.Clear();
        Assert.Empty(history.Strokes);

        history.Undo();
        Assert.Equal(["a", "b"], history.Strokes);

        history.Redo();
        Assert.Empty(history.Strokes);

        history.Undo();
        history.Undo(); // now undo the "b" stroke
        Assert.Equal(["a"], history.Strokes);
    }

    [Fact]
    public void Drawing_after_undo_drops_the_redo_branch()
    {
        var history = new StrokeHistory<string>();
        history.Add("a");
        history.Add("b");
        history.Undo();

        history.Add("c");

        Assert.Equal(["a", "c"], history.Strokes);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void Erasing_is_one_undo_step_and_restores_original_order()
    {
        var history = new StrokeHistory<string>();
        foreach (string s in new[] { "a", "b", "c", "d" }) history.Add(s);

        history.Remove(["d", "b", "b", "zzz"]); // one drag touched d and b (b twice); zzz isn't a stroke
        Assert.Equal(["a", "c"], history.Strokes);

        history.Undo();
        Assert.Equal(["a", "b", "c", "d"], history.Strokes);

        history.Redo();
        Assert.Equal(["a", "c"], history.Strokes);
    }

    [Fact]
    public void Removing_nothing_is_not_an_undo_step()
    {
        var history = new StrokeHistory<string>();
        history.Add("a");
        history.Remove(["missing"]);
        history.Undo();
        Assert.Empty(history.Strokes); // the undo took back the Add, not an empty erase
    }

    [Theory]
    [InlineData(5, 3, true)]    // just above the middle of the first segment
    [InlineData(10, 8, true)]   // near the corner
    [InlineData(5, 7, false)]   // too far from both segments
    [InlineData(-6, 0, false)]  // past the start, beyond the radius
    public void Hit_testing_measures_distance_to_the_line(double x, double y, bool expected)
    {
        (double, double)[] stroke = [(0, 0), (10, 0), (10, 10)];
        Assert.Equal(expected, StrokeGeometry.IsNear(stroke, x, y, radius: 4));
    }

    [Fact]
    public void Nothing_to_do_is_a_no_op()
    {
        var history = new StrokeHistory<string>();
        int changes = 0;
        history.Changed += (_, _) => changes++;

        history.Undo();
        history.Redo();
        history.Clear(); // nothing to clear: not an undo step either

        Assert.Equal(0, changes);
        Assert.False(history.CanUndo);
    }
}
