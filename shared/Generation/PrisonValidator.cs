using Prison.Shared.World;

namespace Prison.Shared.Generation;

public sealed record ValidationReport(bool Passed, IReadOnlyList<string> Failures);

/// <summary>
/// Structural/functional validation (PLAN §8.4.A, binary pass/fail): the prison must simply
/// *work as a facility* — prisoners reach food/cells/work/medical, staff reach everything,
/// no orphaned rooms, sane size. Escapability is deliberately NOT tested (Pillar #9): a
/// candidate is never rejected for being too hard to escape.
/// </summary>
public static class PrisonValidator
{
    /// <summary>Performance guard: keep candidate worlds within the simulation budget (§8.4.A).</summary>
    public const int MaxWalkableTiles = 50_000;

    public static ValidationReport Validate(MapDefinition map, TileRegistry tiles)
    {
        var failures = new List<string>();
        var world = map.BuildWorld(tiles);

        if (map.Rooms.Count == 0)
            failures.Add("no room metadata: generated maps must carry their room placements");

        // Locked doors block until opened; unlocked/absent doors are traversable in play.
        var locked = map.Doors.Where(d => d.Locked).Select(d => d.Position).ToHashSet();

        var spawn = map.PlayerSpawn.Position;
        if (!world.IsWalkable(spawn))
            failures.Add($"player spawn {spawn} is not walkable");

        var reachable = GridFlood.From(world, spawn, locked);
        if (reachable.Count == 0)
            failures.Add("spawn flood fill found no reachable tiles");
        if (reachable.Count > MaxWalkableTiles)
            failures.Add($"world too large: {reachable.Count} reachable tiles (budget {MaxWalkableTiles})");

        // Every cell must be reachable (prisoners live there; staff search/escort there).
        foreach (var room in map.Rooms.Where(r => r.Type == "cell_block"))
        {
            foreach (var tile in InteriorWalkable(world, room))
            {
                if (!reachable.Contains(tile))
                {
                    failures.Add($"cell block '{room.Id}' tile {tile} unreachable from spawn");
                    break;
                }
            }
        }

        // Per the schedule (§8.4.A): food, work, medical — and staff posts — must be reachable.
        foreach (var type in new[] { "cafeteria", "kitchen", "workshop", "medical", "guard_station" })
        {
            foreach (var room in map.Rooms.Where(r => r.Type == type))
            {
                var interior = InteriorWalkable(world, room);
                if (interior.Count == 0)
                    failures.Add($"{type} '{room.Id}' has no walkable interior");
                else if (!interior.Any(reachable.Contains))
                    failures.Add($"{type} '{room.Id}' unreachable from spawn");
            }
        }

        // Guards must be able to stand where they spawn and walk their routes.
        foreach (var guard in map.Guards)
        {
            if (!reachable.Contains(guard.Position))
                failures.Add($"guard spawn {guard.Position} unreachable");
            foreach (var wp in guard.Patrol)
            {
                var waypoint = new TilePos(wp[0], wp[1], guard.Floor);
                if (!reachable.Contains(waypoint))
                    failures.Add($"patrol waypoint {waypoint} unreachable");
            }
        }

        return new ValidationReport(failures.Count == 0, failures);
    }

    /// <summary>Walkable tiles strictly inside a room's rect (excluding its wall border).</summary>
    public static List<TilePos> InteriorWalkable(WorldGrid world, MapDefinition.MapRoom room)
    {
        var interior = new List<TilePos>();
        for (var y = room.Y0 + 1; y < room.Y1; y++)
            for (var x = room.X0 + 1; x < room.X1; x++)
            {
                var pos = new TilePos(x, y, room.Floor);
                if (world.IsWalkable(pos))
                    interior.Add(pos);
            }

        return interior;
    }
}

/// <summary>Same-floor 4-neighbour flood fill over walkability (stairs handled from Phase 6 on).</summary>
internal static class GridFlood
{
    public static HashSet<TilePos> From(WorldGrid world, TilePos start, IReadOnlySet<TilePos>? blocked = null)
    {
        var reached = new HashSet<TilePos>();
        if (!world.IsWalkable(start))
            return reached;

        var queue = new Queue<TilePos>();
        queue.Enqueue(start);
        reached.Add(start);
        Span<(int dx, int dy)> directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

        while (queue.TryDequeue(out var current))
        {
            foreach (var (dx, dy) in directions)
            {
                var next = new TilePos(current.X + dx, current.Y + dy, current.Floor);
                if (reached.Contains(next) || !world.IsWalkable(next))
                    continue;
                if (blocked is not null && blocked.Contains(next))
                    continue;
                reached.Add(next);
                queue.Enqueue(next);
            }
        }

        return reached;
    }

    /// <summary>BFS step distance from start to the nearest tile of a target set (-1 if unreachable).</summary>
    public static int Distance(WorldGrid world, TilePos start, IReadOnlySet<TilePos> targets)
    {
        if (targets.Contains(start))
            return 0;
        var seen = new HashSet<TilePos> { start };
        var queue = new Queue<(TilePos Pos, int Dist)>();
        queue.Enqueue((start, 0));
        Span<(int dx, int dy)> directions = [(1, 0), (-1, 0), (0, 1), (0, -1)];

        while (queue.TryDequeue(out var current))
        {
            foreach (var (dx, dy) in directions)
            {
                var next = new TilePos(current.Pos.X + dx, current.Pos.Y + dy, current.Pos.Floor);
                if (seen.Contains(next) || !world.IsWalkable(next))
                    continue;
                if (targets.Contains(next))
                    return current.Dist + 1;
                seen.Add(next);
                queue.Enqueue((next, current.Dist + 1));
            }
        }

        return -1;
    }
}
