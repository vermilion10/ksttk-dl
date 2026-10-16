using System.Text.Json;

namespace KsttkDl.Core.Room;

/// <summary>
/// one frame of a sprite sheet. <paramref name="Frame"/> is where it sits in the sheet; a trimmed frame lost
/// transparent margins, so it is drawn <paramref name="OffsetX"/>, <paramref name="OffsetY"/> into a
/// <paramref name="SourceWidth"/> × <paramref name="SourceHeight"/> box
/// </summary>
public sealed record AtlasFrame(string Name, SpriteRect Frame, int OffsetX, int OffsetY, int SourceWidth, int SourceHeight);

/// <summary>a TexturePacker sprite sheet as the client uses them: a png and a json of named frames.</summary>
public sealed class Atlas
{
    private Atlas(string imagePath, IReadOnlyDictionary<string, AtlasFrame> frames)
    {
        ImagePath = imagePath;
        Frames = frames;
    }

    public string ImagePath { get; }

    public IReadOnlyDictionary<string, AtlasFrame> Frames { get; }

    /// <summary>reads the frame table; null when the json is missing or not a sprite sheet.</summary>
    public static Atlas? Load(string jsonPath, string imagePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath).TrimStart('﻿'));
            if (!document.RootElement.TryGetProperty("frames", out var table) || table.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var frames = new Dictionary<string, AtlasFrame>(StringComparer.Ordinal);
            foreach (var property in table.EnumerateObject())
            {
                var value = property.Value;
                if (!value.TryGetProperty("frame", out var rect))
                {
                    continue;
                }

                var frame = new SpriteRect(Int(rect, "x"), Int(rect, "y"), Int(rect, "w"), Int(rect, "h"));
                var (x, y) = value.TryGetProperty("spriteSourceSize", out var trim) ? (Int(trim, "x"), Int(trim, "y")) : (0, 0);
                var (w, h) = value.TryGetProperty("sourceSize", out var size)
                    ? (Int(size, "w"), Int(size, "h"))
                    : (frame.Width, frame.Height);
                frames[property.Name] = new AtlasFrame(property.Name, frame, x, y, w, h);
            }

            return new Atlas(imagePath, frames);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>the sprite for a frame whose untrimmed box has its top-left corner at (x, y).</summary>
    public RoomSprite? Sprite(string name, int x, int y, double opacity = 1) =>
        Frames.TryGetValue(name, out var frame)
            ? new RoomSprite(ImagePath, frame.Frame, x + frame.OffsetX, y + frame.OffsetY) { Opacity = opacity }
            : null;

    /// <summary>the sprite for a frame centred on (x, y), as the client centres pivots: on the rounded half size.</summary>
    public RoomSprite? Centred(string name, int x, int y) =>
        Frames.TryGetValue(name, out var frame)
            ? Sprite(name, x - (int)Math.Round(frame.SourceWidth / 2.0, MidpointRounding.AwayFromZero),
                y - (int)Math.Round(frame.SourceHeight / 2.0, MidpointRounding.AwayFromZero))
            : null;

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
