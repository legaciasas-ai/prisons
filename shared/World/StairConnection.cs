namespace Prison.Shared.World;

/// <summary>
/// A designated bidirectional connection between two floors (stairs or elevator, PLAN §7.3).
/// These are the only nodes through which pathfinding and entities may change floors.
/// </summary>
public readonly record struct StairConnection(TilePos A, TilePos B)
{
    /// <summary>Extra pathfinding cost of traversing the stairs themselves.</summary>
    public const float TraversalCost = 2f;

    public bool Touches(TilePos pos) => A == pos || B == pos;

    public TilePos OtherEnd(TilePos pos) =>
        pos == A ? B :
        pos == B ? A :
        throw new ArgumentException($"{pos} is not an endpoint of this stair connection");
}
