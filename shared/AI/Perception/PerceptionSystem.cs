using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Visibility;
using Prison.Shared.World;

namespace Prison.Shared.AI.Perception;

/// <summary>
/// Staff vision (PLAN §7.5): each guard, at a rate set by how engaged it is (§7.10 scheduler),
/// computes its vision cone through the *shared* Visibility module and updates its beliefs
/// about the prisoners it can physically see. No raycast, no line of sight ⇒ no detection.
/// </summary>
public sealed class PerceptionSystem(WorldGrid world, EventBus events) : ISimulationSystem
{
    private static readonly QueryDescription Guards =
        new QueryDescription().WithAll<GuardTag, Position, Facing, VisionSense, AiState, Beliefs>();

    private static readonly QueryDescription Prisoners =
        new QueryDescription().WithAll<PrisonerTag, Position, Footsteps>();

    private readonly List<(Entity Entity, TilePos Tile, float Speed, bool Disguised)> _prisoners = [];

    public string Name => "Perception";

    /// <summary>
    /// Within this distance a guard recognizes a face regardless of uniform (PLAN §7.8
    /// disguises): beyond it, a convincingly dressed prisoner reads as a colleague.
    /// </summary>
    public const float DisguiseScrutinyTiles = 3f;

    /// <summary>Perception interval in ticks per activity (PLAN §7.10: chasing updates far more often).</summary>
    private static uint IntervalFor(GuardAction action) => action switch
    {
        GuardAction.Chase or GuardAction.Arrest => 2,
        GuardAction.Investigate => 5,
        _ => 10,
    };

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        _prisoners.Clear();
        ecsWorld.Query(in Prisoners, (Entity entity, ref Position position, ref Footsteps footsteps) =>
        {
            var tile = new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor);
            var disguised = ecsWorld.Has<Items.Appearance>(entity)
                && ecsWorld.Get<Items.Appearance>(entity).DisguiseRole is not null;
            _prisoners.Add((entity, tile, footsteps.ObservableSpeed, disguised));
        });

        var tick = time.Tick;
        var dt = time.DeltaSeconds;

        ecsWorld.Query(in Guards, (Entity guard, ref Position position, ref Facing facing,
            ref VisionSense sense, ref AiState state, ref Beliefs beliefs) =>
        {
            if (tick < state.NextPerceptionTick)
                return;
            var interval = IntervalFor(state.Action);
            state.NextPerceptionTick = tick + interval;

            var origin = new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor);
            var vision = VisionParameters.Cone(sense.MaxDistance, sense.DarkDistance, facing.Radians, sense.FovDegrees);
            var visibleTiles = FieldOfView.Compute(world, origin, vision);

            foreach (var (prisoner, tile, speed, disguised) in _prisoners)
            {
                var visible = tile.Floor == origin.Floor && visibleTiles.Contains(tile);

                // A disguise defeats identification at a distance, but not a close-up look —
                // resolved from the same physical sighting, never from hidden state (§7.8).
                if (visible && disguised
                    && TilePos.EuclideanDistance(origin, tile) > DisguiseScrutinyTiles)
                {
                    visible = false;
                }

                if (visible)
                {
                    var running = speed >= Footsteps.RunSpeedThreshold;
                    var restricted = world.IsRestricted(tile);

                    if (!beliefs.Suspects.TryGetValue(prisoner, out var belief))
                        beliefs.Suspects[prisoner] = belief = new SuspectBelief();

                    if (disguised && !belief.SeenThroughDisguise)
                    {
                        belief.SeenThroughDisguise = true;
                        events.Publish(new DisguiseCompromisedEvent(tick, guard, prisoner, tile));
                    }

                    var moved = belief.LastKnown != tile;
                    belief.LastKnown = tile;
                    belief.LastSeenTick = tick;
                    belief.Confidence = 1f;
                    var appeared = !belief.CurrentlyVisible;
                    belief.CurrentlyVisible = true;

                    events.Publish(new PrisonerObservedEvent(
                        tick, guard, prisoner, tile, running, restricted, interval * dt));

                    if (appeared || moved)
                        state.DecisionRequested = true;
                }
                else if (beliefs.Suspects.TryGetValue(prisoner, out var belief) && belief.CurrentlyVisible)
                {
                    belief.CurrentlyVisible = false;
                    state.DecisionRequested = true; // lost sight — rethink
                }
            }
        });
    }
}
