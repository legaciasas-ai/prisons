using Prison.Shared.Pathfinding;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

[Collection("content")]
public class PathfindingTests(TestContent content)
{
    [Fact]
    public void AStar_FindsPathAroundWalls()
    {
        var world = content.BuildWorld();
        var start = new TilePos(5, 4, 0);  // inside one cell
        var goal = new TilePos(20, 4, 0);  // inside another cell, 5 walls away

        var path = AStar.FindPath(world, start, goal);

        Assert.NotNull(path);
        Assert.Equal(start, path![0]);
        Assert.Equal(goal, path[^1]);
        Assert.All(path, p => Assert.True(world.IsWalkable(p)));
        AssertContinuous(path);
        // Must detour down through both cell doors and along the corridor,
        // so strictly longer than the straight manhattan distance.
        Assert.True(path.Count - 1 > (int)TilePos.ManhattanDistance(start, goal));
    }

    [Fact]
    public void AStar_ReturnsNullWhenUnreachable()
    {
        var world = content.BuildWorld();
        // The perimeter fence tile itself is not walkable.
        Assert.Null(AStar.FindPath(world, new TilePos(5, 4, 0), new TilePos(0, 0, 0)));
    }

    [Fact]
    public void AStar_TrivialPathForSameStartAndGoal()
    {
        var world = content.BuildWorld();
        var start = new TilePos(10, 7, 0);
        var path = AStar.FindPath(world, start, start);
        Assert.NotNull(path);
        Assert.Single(path!);
    }

    [Fact]
    public void HierarchicalPathfinder_RoutesAcrossFloorsViaStairs()
    {
        var world = content.BuildWorld();
        var pathfinder = new HierarchicalPathfinder(world);
        var start = content.Map.PlayerSpawn.Position;     // cell, floor 0
        var goal = new TilePos(20, 5, 1);                 // east office, floor 1

        var path = pathfinder.FindPath(start, goal);

        Assert.NotNull(path);
        Assert.Equal(start, path![0]);
        Assert.Equal(goal, path[^1]);
        AssertContinuous(path);

        // Exactly one floor change, happening at the stair connection.
        var hops = new List<(TilePos from, TilePos to)>();
        for (var i = 1; i < path.Count; i++)
            if (path[i].Floor != path[i - 1].Floor)
                hops.Add((path[i - 1], path[i]));

        var hop = Assert.Single(hops);
        Assert.Equal(new TilePos(25, 12, 0), hop.from);
        Assert.Equal(new TilePos(25, 12, 1), hop.to);
    }

    [Fact]
    public void HierarchicalPathfinder_SameFloorFallsBackToDirectAStar()
    {
        var world = content.BuildWorld();
        var pathfinder = new HierarchicalPathfinder(world);

        var path = pathfinder.FindPath(new TilePos(6, 7, 0), new TilePos(20, 7, 0));

        Assert.NotNull(path);
        Assert.All(path!, p => Assert.Equal(0, p.Floor));
    }

    [Fact]
    public void PathfindingService_ServesHigherPriorityFirstWithinBudget()
    {
        var world = content.BuildWorld();
        var service = new PathfindingService(new HierarchicalPathfinder(world));

        var low = service.Request(new TilePos(6, 7, 0), new TilePos(20, 7, 0), priority: 10);
        var high = service.Request(new TilePos(6, 8, 0), new TilePos(20, 8, 0), priority: 90);

        Assert.Equal(1, service.Process(budget: 1));
        Assert.Equal(PathRequestStatus.Completed, high.Status);
        Assert.Equal(PathRequestStatus.Pending, low.Status);
        Assert.Equal(1, service.PendingCount);

        service.Process(budget: 10);
        Assert.Equal(PathRequestStatus.Completed, low.Status);
    }

    /// <summary>Every step is a 4-neighbour move on one floor, or a stair hop between floors.</summary>
    private static void AssertContinuous(List<TilePos> path)
    {
        for (var i = 1; i < path.Count; i++)
        {
            var a = path[i - 1];
            var b = path[i];
            if (a.Floor == b.Floor)
                Assert.Equal(1, Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y));
            else
                Assert.Equal((a.X, a.Y), (b.X, b.Y)); // stairs connect vertically aligned tiles in this map
        }
    }
}
