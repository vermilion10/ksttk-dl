using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsttkDl.Core.Room;

/// <summary>a layer's edits on top of the game's placement.</summary>
public sealed record LayerTweak
{
    public int OffsetX { get; init; }

    public int OffsetY { get; init; }

    public double Opacity { get; init; } = 1;

    /// <summary>size around the layer's centre; 1 is as the game draws it.</summary>
    public double Scale { get; init; } = 1;

    public bool Hidden { get; init; }
}

/// <summary>a saved room: furniture per slot, the secretary, the view outside and any layer edits.</summary>
public sealed record RoomPreset
{
    public string Name { get; init; } = "";

    /// <summary>furniture id per slot, keyed by slot name; a missing slot stays empty.</summary>
    public Dictionary<FurnitureSlot, int> Furniture { get; init; } = [];

    public int SecretaryId { get; init; }

    public bool Damaged { get; init; }

    /// <summary>which picture the secretary uses (a SecretaryArt key); rooms saved before it existed follow Damaged.</summary>
    public string? SecretaryArt { get; init; }

    /// <summary>the port interface over the room, or null to leave it off.</summary>
    public PortUiSettings? PortUi { get; init; }

    public bool LeftOfCentre { get; init; }

    public TimeOfDay Time { get; init; } = TimeOfDay.Day;

    /// <summary>edits keyed by layer name (the slot names, "Outside" and "Secretary").</summary>
    public Dictionary<string, LayerTweak> Layers { get; init; } = [];
}

/// <summary>the port interface's skin and what its header and clock say.</summary>
public sealed record PortUiSettings
{
    public PortSkin Skin { get; init; } = PortSkin.Skin1;

    public PortUiValues Values { get; init; } = new();
}

/// <summary>saved rooms, kept as one json file.</summary>
public sealed class RoomPresetStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<RoomPreset> Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<RoomPreset>>(File.ReadAllText(path), Options) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>adds or replaces the preset with the same name.</summary>
    public void Save(RoomPreset preset)
    {
        var presets = Load().Where(p => !p.Name.Equals(preset.Name, StringComparison.OrdinalIgnoreCase)).Append(preset)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        Write(presets);
    }

    public void Delete(string name) =>
        Write(Load().Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList());

    private void Write(List<RoomPreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(presets, Options));
    }
}
