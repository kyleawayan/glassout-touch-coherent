using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassoutTouch;

/// <summary>A GlassOut profile, as stored in %APPDATA%\GlassOut\profiles\*.json.</summary>
public sealed class Profile
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("settings")] public ProfileSettings? Settings { get; set; }
    [JsonPropertyName("layout")] public Layout? Layout { get; set; }

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };

    public static Profile Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Profile>(json, Opts)
               ?? throw new InvalidDataException($"Could not parse profile: {path}");
    }
}

public sealed class ProfileSettings
{
    [JsonPropertyName("touchClickDelayMs")] public int? TouchClickDelayMs { get; set; }
}

public sealed class Layout
{
    [JsonPropertyName("placements")] public List<Placement> Placements { get; set; } = new();
    [JsonPropertyName("screens")] public List<ScreenInfo> Screens { get; set; } = new();
}

/// <summary>A monitor as GlassOut sees it — bounds are in scaled DIP units, not physical pixels.</summary>
public sealed class ScreenInfo
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}

public sealed class Placement
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("sourceId")] public string? SourceId { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("x")] public int X { get; set; }
    [JsonPropertyName("y")] public int Y { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
}
