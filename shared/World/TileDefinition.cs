using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prison.Shared.World;

/// <summary>
/// A tile is a bag of properties defined in data, never a hardcoded type (Pillar #4, PLAN §7.2).
/// Engine code only ever asks a tile "what are your properties", never "are you concrete".
/// Schema: PLAN §17.1 (extended with <see cref="Walkable"/>).
/// </summary>
public sealed record TileDefinition
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public required string Id { get; init; }

    public string DisplayName { get; init; } = "";

    public bool Walkable { get; init; }

    public float MovementCost { get; init; } = 1f;

    /// <summary>0 = fully blocks line of sight, 1 = fully transparent (PLAN §7.4).</summary>
    public float VisibilityTransparency { get; init; } = 1f;

    public float SoundTransmission { get; init; } = 0.5f;

    public bool CanDig { get; init; }

    public bool CanBurn { get; init; }

    public bool CanPlaceFurniture { get; init; }

    public bool CanFlood { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonIgnore]
    public bool BlocksSight => VisibilityTransparency < 0.5f;

    public bool HasTag(string tag) => Tags.Contains(tag);
}
