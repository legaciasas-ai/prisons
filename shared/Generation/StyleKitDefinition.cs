using System.Text.Json;
using Prison.Shared.World;

namespace Prison.Shared.Generation;

/// <summary>
/// An Architectural Style Kit (PLAN §8.2, §17.3): *how it is built*, purely visually.
/// Applied by swapping the materials a map's glyph legend points at — the same functional
/// layout in two kits is gameplay-identical but visually unrecognizable as the same room.
/// The referenced tiles must be gameplay-equivalent to the defaults (visuals only, enforced
/// by test).
/// </summary>
public sealed record StyleKitDefinition
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = "";

    public required string WallMaterial { get; init; }

    public required string FloorMaterial { get; init; }

    public string WindowMaterial { get; init; } = "glass_wall";

    /// <summary>Rendering hint for the client (hex colors) — the simulation never reads it.</summary>
    public IReadOnlyList<string> Palette { get; init; } = [];

    public static IReadOnlyList<StyleKitDefinition> LoadFromDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)
            .Select(file => JsonSerializer.Deserialize<StyleKitDefinition>(
                File.ReadAllText(file), TileDefinition.JsonOptions)
                ?? throw new InvalidDataException($"Empty style kit: {file}"))
            .ToList();
}

/// <summary>
/// A Decoration Rule Set (PLAN §8.2, §17.4): *how it feels lived-in* — seeded, sparse tile
/// swaps (cracks, bushes) that never change a tile's walkability class (cosmetic only).
/// </summary>
public sealed record DecorationRuleSet
{
    public required string Id { get; init; }

    public string DisplayName { get; init; } = "";

    public required List<DecorationRule> Rules { get; init; }

    public sealed record DecorationRule
    {
        public required string TargetTile { get; init; }
        public required string ReplacementTile { get; init; }
        public float Chance { get; init; }
    }

    public static IReadOnlyList<DecorationRuleSet> LoadFromDirectory(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal)
            .Select(file => JsonSerializer.Deserialize<DecorationRuleSet>(
                File.ReadAllText(file), TileDefinition.JsonOptions)
                ?? throw new InvalidDataException($"Empty decoration rule set: {file}"))
            .ToList();
}
