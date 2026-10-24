using PinPoint.Core.Text;

namespace PinPoint.Core.Tests;

public class RecognizedTextTests
{
    private static (string, TextBox) W(string text, double x, double y, double width = 40, double height = 12) =>
        (text, new TextBox(x, y, width, height));

    private static IReadOnlyList<(string, TextBox)> Line(params (string, TextBox)[] words) => words;

    [Fact]
    public void Paragraph_lines_join_with_new_lines_and_words_with_spaces()
    {
        var text = new RecognizedText(
        [
            Line(W("second", 0, 20), W("line", 50, 20)), // given out of order on purpose
            Line(W("Hello", 0, 0), W("world", 50, 0)),
        ]);

        Assert.Equal($"Hello world{Environment.NewLine}second line", text.AllText);
    }

    [Fact]
    public void Side_by_side_cells_become_tab_separated_table_rows()
    {
        // OCR reports each cell of a 2×3 table as its own line, in no useful order.
        var text = new RecognizedText(
        [
            Line(W("Zi", 200, 31), W("Hour", 230, 31)),
            Line(W("Anecdote", 0, 0, 80)),
            Line(W("Lu", 100, 30), W("Wenxin", 125, 30)),
            Line(W("Where", 200, 1)),
            Line(W("Mutual", 0, 30), W("Growth", 50, 30)),
            Line(W("NPC", 100, 0)),
        ]);

        Assert.Equal($"Anecdote\tNPC\tWhere{Environment.NewLine}Mutual Growth\tLu Wenxin\tZi Hour", text.AllText);
    }

    [Fact]
    public void Selection_covers_a_reading_order_range_in_either_direction()
    {
        var text = new RecognizedText(
        [
            Line(W("one", 0, 0), W("two", 50, 0), W("three", 100, 0)),
            Line(W("four", 0, 20), W("five", 50, 20)),
        ]);

        Assert.Equal($"two three{Environment.NewLine}four", text.GetText(1, 3));
        Assert.Equal($"two three{Environment.NewLine}four", text.GetText(3, 1));
        Assert.Equal("five", text.GetText(4, 99)); // clamped
    }

    [Fact]
    public void Hit_testing_finds_the_word_under_or_nearest_the_pointer()
    {
        var text = new RecognizedText([Line(W("left", 0, 0), W("right", 100, 0))]);

        Assert.Equal(1, text.HitTest(110, 5));                         // inside "right"
        Assert.Equal(-1, text.HitTest(70, 5));                         // in the gap, no tolerance
        Assert.Equal(0, text.HitTest(55, 5, double.PositiveInfinity)); // nearest while dragging
        Assert.Equal(-1, RecognizedText.Empty.HitTest(0, 0, double.PositiveInfinity));
    }

    [Fact]
    public void Blank_words_and_empty_lines_are_ignored()
    {
        var text = new RecognizedText([Line(W(" ", 0, 0)), Line(), Line(W("ok", 0, 20))]);
        Assert.Equal("ok", text.AllText);
        Assert.True(RecognizedText.Empty.IsEmpty);
        Assert.Equal(string.Empty, RecognizedText.Empty.AllText);
    }
}
