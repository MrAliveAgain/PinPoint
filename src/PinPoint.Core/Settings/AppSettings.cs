using System.Text.Json.Serialization;
using PinPoint.Core.Boards;

namespace PinPoint.Core.Settings;

/// <summary>
/// Persisted user preferences. Add new properties with sensible defaults; unknown/missing
/// JSON members are tolerated so old and new versions can read each other's files.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Command id → shortcut text. Missing = command default, empty string = unassigned.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where snips are saved; null means Pictures\PinPoint.</summary>
    public string? SnipFolder { get; set; }

    public bool SaveSnips { get; set; } = true;

    public bool CopySnipsToClipboard { get; set; } = true;

    public bool HighlightPinnedWindows { get; set; } = true;

    /// <summary>#RRGGBB border colour for pinned windows.</summary>
    public string HighlightColor { get; set; } = "#4CC2FF";

    /// <summary>When false (default), clicking a pin never steals focus from the game underneath.</summary>
    public bool PinsTakeFocus { get; set; }

    public bool ShowNotifications { get; set; } = true;
    /// <summary>One-time hint that closing the window keeps PinPoint in the tray.</summary>
    public bool HasShownTrayHint { get; set; }

    /// <summary>Board that was open when PinPoint last exited; reopened on start. Null = none.</summary>
    public string? OpenBoardId { get; set; }

    /// <summary>Last choice for "include pinned windows" when exporting a board as an image.</summary>
    public bool ExportIncludesPinnedWindows { get; set; }

    /// <summary>Last choice for "pack together" when exporting a board as an image.</summary>
    public bool ExportPacked { get; set; }

    /// <summary>What's behind the pins in exported and copied board images.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<BackgroundStyle>))]
    public BackgroundStyle ExportBackground { get; set; } = BackgroundStyle.Default;

    /// <summary>#RRGGBB for the solid-colour background.</summary>
    public string ExportBackgroundColor { get; set; } = "#2B2B2B";

    /// <summary>The picture for the image background.</summary>
    public string? ExportBackgroundImage { get; set; }

    /// <summary>Repeat the background picture as a tile instead of filling with it.</summary>
    public bool ExportBackgroundTile { get; set; }

    [JsonIgnore]
    public CollageBackground ExportBackgroundChoice =>
        new(ExportBackground, ExportBackgroundColor, ExportBackgroundImage, ExportBackgroundTile);

    /// <summary>Whether the board preview shows pins packed together (true) or where they were on screen.</summary>
    public bool PreviewPacked { get; set; } = true;

    /// <summary>How many times a snip has pointed out "text found, click T". Stops after a few.</summary>
    public int TextHintsShown { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
