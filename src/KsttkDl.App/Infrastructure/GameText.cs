using System.Globalization;
using System.Windows;
using System.Windows.Media;
using KsttkDl.App.ViewModels;
using KsttkDl.Core.Fonts;
using KsttkDl.Core.Room;

namespace KsttkDl.App.Infrastructure;

/// <summary>
/// draws the port's text in the game's own font, as pictures, so the room canvas and the exported png treat
/// text like any other sprite. lines are placed the way the client's PIXI text places them: the top of a line
/// sits the font's measured ascent above the baseline, and right-anchored text ends at its x
/// </summary>
public static class GameText
{
    // the string PIXI measures a font's ascent with
    private const string MetricsSample = "|ÉqÅ";

    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");
    private static readonly FontFamily Fallback = new("Yu Gothic UI, Meiryo UI, Segoe UI");
    private static readonly Dictionary<bool, FontFamily> Families = [];

    /// <summary>true once the game's font is in use rather than the system fallback.</summary>
    public static bool HasGameFont => Families.Count > 0 && Families.Values.All(f => !ReferenceEquals(f, Fallback));

    /// <summary>picks up the unpacked game fonts; call again after they are downloaded.</summary>
    public static void Load(GameFonts fonts)
    {
        foreach (var bold in new[] { false, true })
        {
            Families[bold] = fonts.Load(bold) is { } font
                ? new FontFamily(new Uri(Path.GetDirectoryName(font.Path)! + Path.DirectorySeparatorChar), "./#" + font.Family)
                : Fallback;
        }
    }

    public static SpriteImage? Render(RoomText text)
    {
        if (text.Text.Length == 0)
        {
            return null;
        }

        var family = Families.GetValueOrDefault(text.Bold, Fallback);
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var brush = new SolidColorBrush(Color.FromRgb((byte)(text.Color >> 16), (byte)(text.Color >> 8), (byte)text.Color));
        brush.Freeze();

        var formatted = Format(text.Text, typeface, text.Size, brush);
        var sample = Format(MetricsSample, typeface, text.Size, brush);
        var inkTop = sample.BuildGeometry(new Point(0, 0)).Bounds.Top;
        var ascent = Math.Ceiling(sample.Baseline - (double.IsFinite(inkTop) ? inkTop : 0));

        var width = formatted.WidthIncludingTrailingWhitespace;
        var height = formatted.Height;
        var group = new DrawingGroup();
        using (var context = group.Open())
        {
            // an invisible box fixes the picture's bounds to the line box rather than to the ink
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
            context.DrawText(formatted, new Point(0, 0));
        }

        var image = new DrawingImage(group);
        image.Freeze();

        var x = text.Anchor switch
        {
            TextAnchor.Right => text.X - width,
            TextAnchor.Centre => text.X - width / 2,
            _ => text.X,
        };
        return new SpriteImage(image, x, text.Y + ascent - formatted.Baseline, width, height);
    }

    private static FormattedText Format(string text, Typeface typeface, double size, Brush brush) =>
        new(text, Japanese, FlowDirection.LeftToRight, typeface, size, brush, 1.0);
}
