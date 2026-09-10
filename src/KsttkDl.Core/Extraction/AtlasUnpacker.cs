using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace KsttkDl.Core.Extraction;

public sealed record AtlasFrame(
    string Name,
    Rectangle Source,
    bool Rotated,
    Point Offset,
    Size SourceSize);

/// <summary>unpacks the TexturePacker-style <c>.json</c> + <c>.png</c> sheet pairs the game's ui ships as, cropping each named frame into its own png. </summary>
public static class AtlasUnpacker
{
    /// <summary>reads the frame table. <c>frames</c> is either an object keyed by frame name (what the game ships) or an array of objects carrying a "filename"; both are accepted. null means the json is not an atlas, which is how a plain data <c>.json</c> next to a <c>.png</c> is told apart from a real sheet. </summary>
    public static IReadOnlyList<AtlasFrame>? ReadFrames(string jsonPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(jsonPath));
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("frames", out var frames))
            {
                return null;
            }

            var result = new List<AtlasFrame>();

            switch (frames.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in frames.EnumerateObject())
                    {
                        var frame = ReadFrame(property.Name, property.Value);
                        if (frame is not null)
                        {
                            result.Add(frame);
                        }
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var element in frames.EnumerateArray())
                    {
                        var name = element.TryGetProperty("filename", out var f) ? f.GetString() : null;
                        var frame = ReadFrame(name ?? $"frame_{result.Count}", element);
                        if (frame is not null)
                        {
                            result.Add(frame);
                        }
                    }

                    break;

                default:
                    return null;
            }

            return result.Count == 0 ? null : result;
        }
    }

    private static AtlasFrame? ReadFrame(string name, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("frame", out var frame)
            || frame.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var x = Int(frame, "x");
        var y = Int(frame, "y");
        var w = Int(frame, "w");
        var h = Int(frame, "h");

        if (w <= 0 || h <= 0)
        {
            return null;
        }

        var rotated = element.TryGetProperty("rotated", out var r) && r.ValueKind == JsonValueKind.True;

        // a trimmed frame lost its transparent border when packed; spriteSourceSize says  where it sits inside the original canvas
        var offset = Point.Empty;
        var sourceSize = new Size(w, h);

        if (element.TryGetProperty("spriteSourceSize", out var sss) && sss.ValueKind == JsonValueKind.Object)
        {
            offset = new Point(Int(sss, "x"), Int(sss, "y"));
        }

        if (element.TryGetProperty("sourceSize", out var ss) && ss.ValueKind == JsonValueKind.Object)
        {
            var sw = Int(ss, "w");
            var sh = Int(ss, "h");
            if (sw > 0 && sh > 0)
            {
                sourceSize = new Size(sw, sh);
            }
        }

        return new AtlasFrame(name, new Rectangle(x, y, w, h), rotated, offset, sourceSize);
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(v.GetDouble())
            : 0;

    /// <summary>crops every frame into <paramref name="outputDirectory"/>; returns the count.</summary>
    public static int Unpack(
        string sheetPath,
        IReadOnlyList<AtlasFrame> frames,
        string outputDirectory,
        Action<string> log)
    {
        using var sheet = Image.Load<Rgba32>(sheetPath);
        Directory.CreateDirectory(outputDirectory);

        var written = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var frame in frames)
        {
            // a rotated frame sits in the sheet turned 90 degrees clockwise, so the region to lift has w and h swapped.
            var region = frame.Rotated
                ? new Rectangle(frame.Source.X, frame.Source.Y, frame.Source.Height, frame.Source.Width)
                : frame.Source;

            var clipped = Rectangle.Intersect(region, sheet.Bounds);
            if (clipped.Width <= 0 || clipped.Height <= 0)
            {
                log($"    frame {frame.Name} lies outside the sheet, skipped");
                continue;
            }

            using var cropped = sheet.Clone(context =>
            {
                context.Crop(clipped);
                if (frame.Rotated)
                {
                    context.Rotate(RotateMode.Rotate270);
                }
            });

            var name = UniqueName(frame.Name, usedNames);
            var destination = Path.Combine(outputDirectory, name + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (frame.Offset == Point.Empty
                && frame.SourceSize.Width == cropped.Width
                && frame.SourceSize.Height == cropped.Height)
            {
                cropped.Save(destination);
            }
            else
            {
                // restore onto the original canvas so frames of one animation stay aligned
                using var canvas = new Image<Rgba32>(frame.SourceSize.Width, frame.SourceSize.Height);
                canvas.Mutate(context => context.DrawImage(cropped, frame.Offset, 1f));
                canvas.Save(destination);
            }

            written++;
        }

        return written;
    }

    /// <summary>sanitizing can collide two frame names (ntfs also ignores case), so later collisions get a numeric suffix rather than overwriting the earlier frame.  </summary>
    private static string UniqueName(string rawName, HashSet<string> used)
    {
        // names may carry a path ("battle_telop/txt_start"); keep the structure, sanitize each segment.
        var segments = rawName.Split('/', '\\')
            .Select(s => Naming.SafeName.Sanitize(s, "frame"))
            .ToArray();

        var name = string.Join(Path.DirectorySeparatorChar, segments);
        if (used.Add(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{name}_{i}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }
}
