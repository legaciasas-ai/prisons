using Arch.Core;
using Prison.Shared.ECS.Components;
using Prison.Shared.World;

namespace Prison.Shared.ECS.Systems;

/// <summary>
/// Moves player-controlled entities from their input intent, colliding against the tile
/// world. Per-axis resolution: a blocked X move still allows sliding along Y and vice versa.
/// </summary>
public sealed class PlayerMovementSystem(WorldGrid world) : ISimulationSystem
{
    /// <summary>Collision radius of a person, in tiles.</summary>
    public const float BodyRadius = 0.3f;

    /// <summary>Sprint speed multiplier (louder and more suspicious, see Footsteps/Suspicion).</summary>
    public const float RunMultiplier = 1.7f;

    private static readonly QueryDescription Players =
        new QueryDescription().WithAll<Position, PlayerInput, MoveSpeed, AI.Facing>();

    public string Name => "PlayerMovement";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var dt = time.DeltaSeconds;
        ecsWorld.Query(in Players, (ref Position position, ref PlayerInput input, ref MoveSpeed speed, ref AI.Facing facing) =>
        {
            var (moveX, moveY) = Normalize(input.MoveX, input.MoveY);
            var tilesPerSecond = speed.TilesPerSecond * (input.Running ? RunMultiplier : 1f);
            var newX = position.X + moveX * tilesPerSecond * dt;
            var newY = position.Y + moveY * tilesPerSecond * dt;

            if (moveX != 0f || moveY != 0f)
                facing.Radians = MathF.Atan2(moveY, moveX);

            if (CanOccupy(newX, position.Y, position.Floor))
                position.X = newX;
            if (CanOccupy(position.X, newY, position.Floor))
                position.Y = newY;
        });
    }

    /// <summary>Checks the four corners of the body's bounding box against walkability.</summary>
    private bool CanOccupy(float x, float y, int floor)
    {
        ReadOnlySpan<(float ox, float oy)> corners =
            [(-BodyRadius, -BodyRadius), (BodyRadius, -BodyRadius), (-BodyRadius, BodyRadius), (BodyRadius, BodyRadius)];

        foreach (var (ox, oy) in corners)
        {
            var tile = new TilePos((int)MathF.Floor(x + ox), (int)MathF.Floor(y + oy), floor);
            if (!world.IsWalkable(tile))
                return false;
        }

        return true;
    }

    private static (float x, float y) Normalize(float x, float y)
    {
        var lengthSquared = x * x + y * y;
        if (lengthSquared <= 1f)
            return (x, y);
        var length = MathF.Sqrt(lengthSquared);
        return (x / length, y / length);
    }
}
