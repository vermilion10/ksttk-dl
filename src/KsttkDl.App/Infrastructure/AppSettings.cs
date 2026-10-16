using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsttkDl.App.Infrastructure;

public enum NetworkMode
{
    Direct,
    JpRelay,
    Proxy,
}

public enum ThemeChoice
{
    System,
    Light,
    Dark,
}

/// <summary>user preferences, kept as json under %LOCALAPPDATA%\ksttk-dl.</summary>
public sealed class AppSettings
{
    private static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ksttk-dl");

    private static readonly string FilePath = Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ksttk-dl");

    public string DataFolder { get; set; } = Path.Combine(DefaultRoot, "data");

    public string OutputFolder { get; set; } = Path.Combine(DefaultRoot, "output");

    public int World { get; set; } = 1;

    public NetworkMode Network { get; set; } = NetworkMode.Direct;

    public string ProxyUrl { get; set; } = "";

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;

    public bool IncludeRare { get; set; }

    public int Concurrency { get; set; } = 6;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
    }
}
