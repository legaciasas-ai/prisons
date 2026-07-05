using System.Text.Json;

namespace Prison.Shared.World;

/// <summary>
/// Loads tile definitions from data files (<c>content/tiles/*.json</c>) and assigns each a
/// dense numeric id used by the tile layers. Id 0 is always the reserved "empty" tile
/// (void / no wall): not walkable, fully transparent.
/// </summary>
public sealed class TileRegistry
{
    public const ushort EmptyId = 0;

    private readonly List<TileDefinition> _byId = [];
    private readonly Dictionary<string, ushort> _byName = new(StringComparer.Ordinal);

    public TileRegistry()
    {
        Register(new TileDefinition
        {
            Id = "empty",
            DisplayName = "Empty",
            Walkable = false,
            MovementCost = 0f,
            VisibilityTransparency = 1f,
        });
    }

    public int Count => _byId.Count;

    public ushort Register(TileDefinition definition)
    {
        if (_byName.ContainsKey(definition.Id))
            throw new InvalidOperationException($"Duplicate tile id '{definition.Id}'");

        var id = (ushort)_byId.Count;
        _byId.Add(definition);
        _byName.Add(definition.Id, id);
        return id;
    }

    public TileDefinition Get(ushort id) => _byId[id];

    public ushort IdOf(string name) =>
        _byName.TryGetValue(name, out var id)
            ? id
            : throw new KeyNotFoundException($"Unknown tile id '{name}'");

    public bool TryIdOf(string name, out ushort id) => _byName.TryGetValue(name, out id);

    /// <summary>Loads every <c>*.json</c> tile definition in a directory.</summary>
    public static TileRegistry LoadFromDirectory(string directory)
    {
        var registry = new TileRegistry();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var definition = JsonSerializer.Deserialize<TileDefinition>(
                File.ReadAllText(file), TileDefinition.JsonOptions)
                ?? throw new InvalidDataException($"Empty tile definition: {file}");
            registry.Register(definition);
        }

        return registry;
    }
}
