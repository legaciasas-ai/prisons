using Prison.Shared.ECS.Components;
using Prison.Shared.ECS.Systems;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

[Collection("content")]
public class MovementTests(TestContent content)
{
    [Fact]
    public void Player_MovesOnOpenFloor_AndIsStoppedByWalls()
    {
        var world = content.BuildWorld();
        using var sim = new Simulation();
        sim.AddSystem(new PlayerMovementSystem(world));

        // Center of a 2-wide cell (x=5..6); wall at x=7.
        var player = sim.World.Create(
            new Position(5.5f, 4.5f, 0), new PlayerInput { MoveX = 1f }, new MoveSpeed(4.5f), new Prison.Shared.AI.Facing(0f));

        for (var i = 0; i < 40; i++) // 2 simulated seconds — unobstructed would travel 9 tiles
            sim.Tick();

        var position = sim.World.Get<Position>(player);
        Assert.True(position.X > 6.0f, $"should have moved right, got {position.X}");
        Assert.True(position.X <= 7f - PlayerMovementSystem.BodyRadius + 0.001f,
            $"must be stopped by the cell wall at x=7, got {position.X}");
        Assert.Equal(4.5f, position.Y, precision: 3);
    }

    [Fact]
    public void Player_SlidesAlongWallsPerAxis()
    {
        var world = content.BuildWorld();
        using var sim = new Simulation();
        sim.AddSystem(new PlayerMovementSystem(world));

        // Pushing diagonally into the cell's east wall still lets the Y component through.
        var player = sim.World.Create(
            new Position(6.5f, 4.5f, 0), new PlayerInput { MoveX = 1f, MoveY = 1f }, new MoveSpeed(4.5f), new Prison.Shared.AI.Facing(0f));

        for (var i = 0; i < 10; i++)
            sim.Tick();

        var position = sim.World.Get<Position>(player);
        Assert.True(position.Y > 4.6f, "Y movement must not be blocked by an X-axis wall");
    }

    [Fact]
    public void Stairs_MoveEntityBetweenFloors_ConsumingTheIntent()
    {
        var world = content.BuildWorld();
        using var sim = new Simulation();
        sim.AddSystem(new StairTraversalSystem(world));

        var player = sim.World.Create(
            new Position(25.5f, 12.5f, 0), new PlayerInput { UseStairs = true }, new MoveSpeed(4.5f), new Prison.Shared.AI.Facing(0f));

        sim.Tick();

        var position = sim.World.Get<Position>(player);
        Assert.Equal(1, position.Floor);
        Assert.Equal(25.5f, position.X, precision: 3);
        Assert.False(sim.World.Get<PlayerInput>(player).UseStairs);

        // A second tick without a new intent must not bounce the player back down.
        sim.Tick();
        Assert.Equal(1, sim.World.Get<Position>(player).Floor);
    }

    [Fact]
    public void UseStairs_AwayFromStairs_DoesNothing()
    {
        var world = content.BuildWorld();
        using var sim = new Simulation();
        sim.AddSystem(new StairTraversalSystem(world));

        var player = sim.World.Create(
            new Position(10.5f, 7.5f, 0), new PlayerInput { UseStairs = true }, new MoveSpeed(4.5f), new Prison.Shared.AI.Facing(0f));

        sim.Tick();

        Assert.Equal(0, sim.World.Get<Position>(player).Floor);
        Assert.False(sim.World.Get<PlayerInput>(player).UseStairs);
    }
}
