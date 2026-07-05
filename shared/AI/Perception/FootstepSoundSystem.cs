using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.World;

namespace Prison.Shared.AI.Perception;

/// <summary>
/// Movement makes noise (PLAN §7.5): every stride emits a sound event with a radius that
/// depends on observable speed — walking ~2 tiles, running ~8. Applies to everyone equally;
/// guards' own footsteps go through the same physics.
/// </summary>
public sealed class FootstepSoundSystem(EventBus events) : ISimulationSystem
{
    public const float WalkRadius = 2f;
    public const float RunRadius = 8f;

    private static readonly QueryDescription Movers =
        new QueryDescription().WithAll<Position, Footsteps>();

    public string Name => "FootstepSound";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var tick = time.Tick;
        var dt = time.DeltaSeconds;

        ecsWorld.Query(in Movers, (Entity entity, ref Position position, ref Footsteps footsteps) =>
        {
            if (!footsteps.HasPrev)
            {
                footsteps.PrevX = position.X;
                footsteps.PrevY = position.Y;
                footsteps.HasPrev = true;
                return;
            }

            var dx = position.X - footsteps.PrevX;
            var dy = position.Y - footsteps.PrevY;
            footsteps.PrevX = position.X;
            footsteps.PrevY = position.Y;

            var moved = MathF.Sqrt(dx * dx + dy * dy);
            footsteps.ObservableSpeed = moved / dt;
            if (moved <= 0f)
                return;

            footsteps.DistanceAccumulator += moved;
            if (footsteps.DistanceAccumulator < Footsteps.StrideTiles)
                return;
            footsteps.DistanceAccumulator = 0f;

            var running = footsteps.ObservableSpeed >= Footsteps.RunSpeedThreshold;
            var tile = new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor);
            events.Publish(new SoundEmittedEvent(
                tick, tile,
                running ? RunRadius : WalkRadius,
                running ? SoundKind.FootstepRun : SoundKind.FootstepWalk,
                entity));
        });
    }
}
