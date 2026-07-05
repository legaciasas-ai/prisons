using Arch.Core;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.World;

namespace Prison.Shared.ECS.Systems;

/// <summary>
/// Declares an escape when a prisoner physically reaches the edge of the map — beyond every
/// fence, wall and yard (PLAN §10.2's "valid escape" trigger). Purely positional: however
/// the player got there (tunnel, cut fence, walked out of the gate in a disguise), the same
/// border crossing counts. Fires once per prisoner per match.
/// </summary>
public sealed class EscapeDetectionSystem(WorldGrid world, EventBus events) : ISimulationSystem
{
    /// <summary>Check cadence — border crossing detection doesn't need per-tick precision.</summary>
    public const uint CheckIntervalTicks = 5;

    private static readonly QueryDescription Prisoners =
        new QueryDescription().WithAll<PrisonerTag, Position>();

    private readonly HashSet<Entity> _escaped = [];

    public string Name => "EscapeDetection";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        if (time.Tick % CheckIntervalTicks != 0)
            return;

        var tick = time.Tick;
        ecsWorld.Query(in Prisoners, (Entity prisoner, ref Position position) =>
        {
            if (_escaped.Contains(prisoner))
                return;

            var floor = world.Floor(position.Floor);
            var x = (int)MathF.Floor(position.X);
            var y = (int)MathF.Floor(position.Y);
            if (x > 0 && y > 0 && x < floor.Width - 1 && y < floor.Height - 1)
                return;

            _escaped.Add(prisoner);
            events.Publish(new EscapeSucceededEvent(tick, prisoner,
                new TilePos(x, y, position.Floor)));
        });
    }
}
