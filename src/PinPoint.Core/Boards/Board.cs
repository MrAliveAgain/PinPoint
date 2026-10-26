using System.Text.Json.Serialization;

namespace PinPoint.Core.Boards;

/// <summary>
/// A saved arrangement of snips and notes you can reopen and keep working on. Positions are screen
/// pixels. Pinned windows are never part of a board: they're live apps that may not exist next time.
/// </summary>
public sealed record Board
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public string Name { get; init; } = "Board";

    public List<BoardSnip> Snips { get; init; } = [];

    public List<BoardNote> Notes { get; init; } = [];

    [JsonIgnore]
    public int ItemCount => Snips.Count + Notes.Count;
}

/// <summary>A snip on a board. The image is <c>{Id}.png</c> in the board's folder.</summary>
/// <param name="Bounds">Where its image sits on screen (client area, current zoom applied).</param>
/// <param name="Strokes">Drawing, each stroke as flat x,y pairs in the snip's own pixels.</param>
public sealed record BoardSnip(string Id, BoardRect Bounds, int ZoomStep, double Opacity, List<double[]> Strokes)
{
    /// <summary>Locked in place: can't be dragged or resized until unlocked.</summary>
    public bool Locked { get; init; }
}

/// <summary>A note on a board. <paramref name="Bounds"/> is the whole note window.</summary>
public sealed record BoardNote(string Id, string Text, BoardRect Bounds, double Opacity)
{
    /// <summary>The .txt the note was saved to with Save/Save as, if any.</summary>
    public string? ExportPath { get; init; }
}

public sealed record BoardRect(int X, int Y, int Width, int Height);

/// <summary>A saved board as listed on the Boards page.</summary>
public sealed record BoardInfo(string Id, string Name, DateTime Modified, int Snips, int Notes);
