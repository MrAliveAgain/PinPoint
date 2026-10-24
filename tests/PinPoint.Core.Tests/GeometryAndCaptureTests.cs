using PinPoint.Core.Capture;
using PinPoint.Core.Geometry;
using PinPoint.Core.Settings;

namespace PinPoint.Core.Tests;

public class GeometryAndCaptureTests
{
    [Fact]
    public void FromPoints_normalises_any_drag_direction()
    {
        Assert.Equal(new PixelRect(10, 20, 30, 40), PixelRect.FromPoints(40, 60, 10, 20));
        Assert.Equal(new PixelRect(10, 20, 30, 40), PixelRect.FromPoints(10, 60, 40, 20));
    }

    [Fact]
    public void Intersect_handles_negative_multi_monitor_coordinates()
    {
        var left = new PixelRect(-1920, 0, 1920, 1080);
        var selection = new PixelRect(-100, 100, 300, 50);
        Assert.Equal(new PixelRect(-100, 100, 100, 50), selection.Intersect(left));
        Assert.True(new PixelRect(0, 0, 5, 5).Intersect(new PixelRect(10, 10, 5, 5)).IsEmpty);
    }

    [Fact]
    public void Crop_copies_the_right_pixels()
    {
        // 4×3 frame at (-2,-1); each pixel's blue channel = its index.
        var pixels = new byte[4 * 3 * 4];
        for (int i = 0; i < 12; i++) pixels[i * 4] = (byte)i;
        var frame = new CapturedFrame(new PixelRect(-2, -1, 4, 3), pixels);

        var crop = frame.Crop(new PixelRect(-1, 0, 2, 2)); // columns 1–2, rows 1–2

        Assert.Equal(new PixelRect(-1, 0, 2, 2), crop.Bounds);
        Assert.Equal([5, 6, 9, 10], Enumerable.Range(0, 4).Select(i => (int)crop.Pixels[i * 4]));
    }

    [Fact]
    public void MakeOpaque_sets_every_alpha_byte()
    {
        var pixels = new byte[4 * 37]; // not a multiple of the vector width
        GdiScreenCapture.MakeOpaque(pixels);
        Assert.All(Enumerable.Range(0, 37), i => Assert.Equal(255, pixels[i * 4 + 3]));
        Assert.All(Enumerable.Range(0, 37), i => Assert.Equal(0, pixels[i * 4]));
    }

    [Theory]
    [InlineData("#4CC2FF", 0x4C, 0xC2, 0xFF)]
    [InlineData("ff8800", 0xFF, 0x88, 0x00)]
    public void Parses_hex_colours(string text, byte r, byte g, byte b)
    {
        Assert.True(HexColor.TryParse(text, out var pr, out var pg, out var pb));
        Assert.Equal((r, g, b), (pr, pg, pb));
    }

    [Fact]
    public void Settings_round_trip_and_survive_corruption()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PinPointTests", Guid.NewGuid().ToString("N"));
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        try
        {
            var settings = new AppSettings { SaveSnips = false, SnipFolder = @"D:\Snips" };
            settings.Hotkeys["snipRegion"] = "Ctrl+Shift+S";
            store.Save(settings);

            var loaded = store.Load();
            Assert.False(loaded.SaveSnips);
            Assert.Equal(@"D:\Snips", loaded.SnipFolder);
            Assert.Equal("Ctrl+Shift+S", loaded.Hotkeys["SNIPREGION"]); // case-insensitive after load

            File.WriteAllText(store.FilePath, "{ not json");
            Assert.True(store.Load().SaveSnips); // defaults
            Assert.True(File.Exists(store.FilePath + ".corrupt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
