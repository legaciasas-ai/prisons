using System.Text.Json;
using Prison.Shared.World;

namespace Prison.Shared.Generation;

/// <summary>
/// A Functional Blueprint (PLAN §8.2, §17.2): a reusable room template defining *how a room
/// works* — walls, floor, door openings, item spawns — independent of visual style (style
/// kits arrive in Phase 6). Loaded from <c>content/rooms/*.json</c>.
///
/// Interior format: a glyph grid using the same conventions as map files ('#' wall, '.' floor,
/// 'm' metal floor, 'g' glass wall), plus 'D' — a doorway on the room's edge (floor + a door
/// entity). Phase 5 grammar: every blueprint exposes its doorway(s) on the *south* edge; the
/// assembler mirrors the room vertically when attaching it from the other side of a corridor.
/// </summary>
public sealed record BlueprintDefinition
{
    public required string Id { get; init; }

    /// <summary>Functional role: cell_block, cafeteria, kitchen, workshop, medical, ...</summary>
    public required string Type { get; init; }

    /// <summary>Inmates this room houses (cell blocks) — 0 for service rooms.</summary>
    public int Capacity { get; init; }

    public required List<string> Rows { get; init; }

    /// <summary>Item spawns, coordinates relative to the room's top-left corner.</summary>
    public List<BlueprintItem> Items { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public int Width => Rows.Max(r => r.Length);

    public int Height => Rows.Count;

    public bool HasTag(string tag) => Tags.Contains(tag);

    public sealed record BlueprintItem
    {
        public required string Id { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
    }

    public static IReadOnlyList<BlueprintDefinition> LoadFromDirectory(string directory)
    {
        var blueprints = new List<BlueprintDefinition>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            blueprints.Add(JsonSerializer.Deserialize<BlueprintDefinition>(
                File.ReadAllText(file), TileDefinition.JsonOptions)
                ?? throw new InvalidDataException($"Empty blueprint definition: {file}"));
        }

        return blueprints;
    }
}
