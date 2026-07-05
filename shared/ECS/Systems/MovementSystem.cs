using Arch.Core;
using Prison.Shared.ECS.Components;

namespace Prison.Shared.ECS.Systems;

using World = Arch.Core.World;

/// <summary>
/// Integrates <see cref="Velocity"/> into <see cref="Position"/> each tick.
/// Collision against the tile world arrives in Phase 1; for now this exists to prove the
/// ECS wiring end-to-end on both client and server.
/// </summary>
public sealed class MovementSystem : ISimulationSystem
{
    private static readonly QueryDescription Moving = new QueryDescription().WithAll<Position, Velocity>();

    public string Name => "Movement";

    public void Update(World world, in SimTime time)
    {
        var dt = time.DeltaSeconds;
        world.Query(in Moving, (ref Position position, ref Velocity velocity) =>
        {
            position.X += velocity.X * dt;
            position.Y += velocity.Y * dt;
        });
    }
}
