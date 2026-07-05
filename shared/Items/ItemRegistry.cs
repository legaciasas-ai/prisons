using System.Text.Json;
using Prison.Shared.World;

namespace Prison.Shared.Items;

/// <summary>Loads item definitions from data files (<c>content/items/*.json</c>).</summary>
public sealed class ItemRegistry
{
    private readonly Dictionary<string, ItemDefinition> _byId = new(StringComparer.Ordinal);

    public int Count => _byId.Count;

    public IEnumerable<ItemDefinition> All => _byId.Values;

    public void Register(ItemDefinition definition)
    {
        if (!_byId.TryAdd(definition.Id, definition))
            throw new InvalidOperationException($"Duplicate item id '{definition.Id}'");
    }

    public ItemDefinition Get(string id) =>
        _byId.TryGetValue(id, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown item id '{id}'");

    public bool TryGet(string id, out ItemDefinition definition) =>
        _byId.TryGetValue(id, out definition!);

    public static ItemRegistry LoadFromDirectory(string directory)
    {
        var registry = new ItemRegistry();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            var definition = JsonSerializer.Deserialize<ItemDefinition>(
                File.ReadAllText(file), TileDefinition.JsonOptions)
                ?? throw new InvalidDataException($"Empty item definition: {file}");
            registry.Register(definition);
        }

        return registry;
    }
}
