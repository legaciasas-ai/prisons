using Prison.Shared.World;

namespace Prison.Shared.Pathfinding;

/// <summary>
/// Grid A* within a single floor (4-directional, tile movement costs from data).
/// Cross-floor routing is layered on top by <see cref="HierarchicalPathfinder"/> (PLAN §7.3).
/// </summary>
public static class AStar
{
    private static readonly (int dx, int dy)[] Neighbors = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    /// <summary>Finds a path from start to goal on one floor. Returns null if unreachable.</summary>
    public static List<TilePos>? FindPath(WorldGrid world, TilePos start, TilePos goal)
    {
        if (start.Floor != goal.Floor)
            throw new ArgumentException("AStar is single-floor; use HierarchicalPathfinder across floors");
        if (!world.IsWalkable(start) || !world.IsWalkable(goal))
            return null;
        if (start == goal)
            return [start];

        var floor = world.Floor(start.Floor);
        int width = floor.Width, height = floor.Height;
        int Index(TilePos p) => p.Y * width + p.X;

        var gScore = new float[width * height];
        Array.Fill(gScore, float.PositiveInfinity);
        var cameFrom = new int[width * height];
        Array.Fill(cameFrom, -1);

        var open = new PriorityQueue<TilePos, float>();
        gScore[Index(start)] = 0f;
        open.Enqueue(start, TilePos.EuclideanDistance(start, goal));

        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal)
                return Reconstruct(cameFrom, current, start, width);

            var currentIndex = Index(current);
            foreach (var (dx, dy) in Neighbors)
            {
                var next = current with { X = current.X + dx, Y = current.Y + dy };
                if (!world.IsWalkable(next))
                    continue;

                var tentative = gScore[currentIndex] + world.MovementCost(next);
                var nextIndex = Index(next);
                if (tentative >= gScore[nextIndex])
                    continue;

                gScore[nextIndex] = tentative;
                cameFrom[nextIndex] = currentIndex;
                open.Enqueue(next, tentative + TilePos.EuclideanDistance(next, goal));
            }
        }

        return null;
    }

    /// <summary>Total movement cost of a found path (cost of entering each tile after the first).</summary>
    public static float PathCost(WorldGrid world, List<TilePos> path)
    {
        var cost = 0f;
        for (var i = 1; i < path.Count; i++)
            cost += world.MovementCost(path[i]);
        return cost;
    }

    private static List<TilePos> Reconstruct(int[] cameFrom, TilePos goal, TilePos start, int width)
    {
        var path = new List<TilePos> { goal };
        var index = goal.Y * width + goal.X;
        while (cameFrom[index] >= 0)
        {
            index = cameFrom[index];
            path.Add(new TilePos(index % width, index / width, start.Floor));
        }

        path.Reverse();
        return path;
    }
}
