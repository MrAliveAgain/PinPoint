namespace PinPoint.Core.Boards;

/// <summary>What's behind the pins in a board image.</summary>
public enum BackgroundStyle
{
    /// <summary>Plain grey that follows the app's light/dark theme.</summary>
    Default,
    Solid,
    DotGrid,
    Transparent,

    /// <summary>A picture of the user's choosing, stretched to fill or repeated as a tile.</summary>
    Image,
}

/// <param name="Color">#RRGGBB, used by <see cref="BackgroundStyle.Solid"/>.</param>
/// <param name="ImagePath">The picture file, used by <see cref="BackgroundStyle.Image"/>.</param>
/// <param name="Tile">Repeat the picture at its own size (a texture) instead of filling the background with it.</param>
public sealed record CollageBackground(BackgroundStyle Style, string Color = "#2B2B2B", string? ImagePath = null, bool Tile = false)
{
    public static CollageBackground Default { get; } = new(BackgroundStyle.Default);

    /// <summary>Names for the export dialog, in the order they're offered.</summary>
    public static IReadOnlyList<(BackgroundStyle Style, string Name)> Choices { get; } =
    [
        (BackgroundStyle.Default, "Default"),
        (BackgroundStyle.Solid, "Solid colour"),
        (BackgroundStyle.DotGrid, "Dot grid"),
        (BackgroundStyle.Transparent, "Transparent"),
        (BackgroundStyle.Image, "Image"),
    ];
}
