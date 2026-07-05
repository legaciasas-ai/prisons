using Prison.Shared.World;

namespace Prison.Shared.Pathfinding;

/// <summary>
/// Hierarchical A* (PLAN §7.3): plan which stair connections to traverse at the floor-graph
/// level, then resolve fine per-floor paths with <see cref="AStar"/>. Handles the general
/// case where even a same-floor goal is only reachable via another floor.
/// </summary>
public sealed class HierarchicalPathfinder(WorldGrid world) : IPathfinder
{
    public List<TilePos>? FindPath(TilePos start, TilePos goal)
    {
        // Fast path: direct same-floor route.
        if (start.Floor == goal.Floor)
        {
            var direct = AStar.FindPath(world, start, goal);
            if (direct is not null)
                return direct;
        }

        return FindViaStairs(start, goal);
    }

    /// <summary>
    /// Dijkstra over a small abstract graph: nodes are the start, the goal, and every stair
    /// endpoint; edges are same-floor A* paths plus the stair traversals themselves.
    /// </summary>
    private List<TilePos>? FindViaStairs(TilePos start, TilePos goal)
    {
        // Node set. Segment cache keys are (from,to) so fine paths are reused for stitching.
        var nodes = new List<TilePos> { start, goal };
        foreach (var stairs in world.Stairs)
        {
            nodes.Add(stairs.A);
            nodes.Add(stairs.B);
        }

        nodes = nodes.Distinct().ToList();
        var segments = new Dictionary<(TilePos, TilePos), List<TilePos>>();

        List<TilePos>? Segment(TilePos from, TilePos to)
        {
            if (segments.TryGetValue((from, to), out var cached))
                return cached;
            var path = AStar.FindPath(world, from, to);
            if (path is not null)
                segments[(from, to)] = path;
            return path;
        }

        var distance = nodes.ToDictionary(n => n, _ => float.PositiveInfinity);
        var previous = new Dictionary<TilePos, TilePos>();
        var viaStairs = new HashSet<TilePos>(); // nodes reached by walking *up/down stairs* rather than a floor path
        var queue = new PriorityQueue<TilePos, float>();
        distance[start] = 0f;
        queue.Enqueue(start, 0f);

        while (queue.TryDequeue(out var current, out var currentDistance))
        {
            if (currentDistance > distance[current])
                continue;
            if (current == goal)
                break;

            // Edges 1: same-floor walks to other nodes.
            foreach (var next in nodes)
            {
                if (next == current || next.Floor != current.Floor)
                    continue;
                var segment = Segment(current, next);
                if (segment is null)
                    continue;
                var cost = currentDistance + AStar.PathCost(world, segment);
                if (cost < distance[next])
                {
                    distance[next] = cost;
                    previous[next] = current;
                    viaStairs.Remove(next);
                    queue.Enqueue(next, cost);
                }
            }

            // Edges 2: stair traversal from this endpoint to its other end.
            var connection = world.StairAt(current);
            if (connection is { } stairs)
            {
                var other = stairs.OtherEnd(current);
                var cost = currentDistance + StairConnection.TraversalCost;
                if (cost < distance[other])
                {
                    distance[other] = cost;
                    previous[other] = current;
                    viaStairs.Add(other);
                    queue.Enqueue(other, cost);
                }
            }
        }

        if (float.IsPositiveInfinity(distance[goal]))
            return null;

        // Reconstruct the node chain, then stitch fine paths.
        var chain = new List<TilePos> { goal };
        while (chain[^1] != start)
            chain.Add(previous[chain[^1]]);
        chain.Reverse();

        var full = new List<TilePos> { start };
        for (var i = 1; i < chain.Count; i++)
        {
            if (viaStairs.Contains(chain[i]) && previous[chain[i]] == chain[i - 1] && chain[i].Floor != chain[i - 1].Floor)
            {
                full.Add(chain[i]); // stair hop: appears as a floor change between endpoints
            }
            else
            {
                var segment = Segment(chain[i - 1], chain[i])!;
                full.AddRange(segment.Skip(1));
            }
        }

        return full;
    }
}
