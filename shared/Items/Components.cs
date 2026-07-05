using Prison.Shared.World;

namespace Prison.Shared.Items;

/// <summary>What an entity carries. Item ids reference <see cref="ItemRegistry"/> data.</summary>
public sealed class Inventory
{
    public const int Capacity = 8;

    public List<string> Items { get; } = [];

    public bool IsFull => Items.Count >= Capacity;

    public bool Has(string itemId) => Items.Contains(itemId);

    public bool HasAll(IReadOnlyList<string> itemIds)
    {
        // Multiset check: two identical ingredients require two carried copies.
        var pool = new List<string>(Items);
        foreach (var id in itemIds)
            if (!pool.Remove(id))
                return false;
        return true;
    }

    public bool Remove(string itemId) => Items.Remove(itemId);
}

/// <summary>An item lying in the world, waiting to be picked up.</summary>
public record struct WorldItem(string ItemId, TilePos Tile);

/// <summary>
/// What an entity outwardly looks like. A worn disguise changes the role observers attribute
/// to it — resolved purely through the perception pipeline, never through hidden state.
/// </summary>
public record struct Appearance(string? DisguiseRole);
