using Prison.Shared.World;

namespace Prison.Shared.Pathfinding;

/// <summary>
/// Pathfinding entry point injected into consumers (PLAN §15: systems receive a pathfinder,
/// they never construct one).
/// </summary>
public interface IPathfinder
{
    /// <summary>Finds a walkable path between any two positions, across floors if needed. Null if unreachable.</summary>
    List<TilePos>? FindPath(TilePos start, TilePos goal);
}
