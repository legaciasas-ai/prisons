using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Pathfinding;
using Prison.Shared.World;

namespace Prison.Shared.AI.Actions;

/// <summary>
/// Executes the current decision (PLAN §7.6 action layer): sets navigation destinations at the
/// right speed/priority, dwells at investigation spots, and performs arrests.
/// Phase 2 simplification (documented): an arrest instantly returns the prisoner to their cell
/// and lowers observed threat — the full escort action arrives with later phases.
/// </summary>
public sealed class AiActionSystem(
    WorldGrid world, PathfindingService pathfinding, TilePos cellSpawn, EventBus events) : ISimulationSystem
{
    public const float PatrolSpeed = 2.5f;
    public const float InvestigateSpeed = 3.2f;
    public const float ChaseSpeed = 5.5f;

    /// <summary>How long a guard looks around at an investigation spot (3s at 20 ticks/s).</summary>
    public const uint InvestigateDwellTicks = 60;

    private static readonly QueryDescription Guards =
        new QueryDescription().WithAll<GuardTag, Position, AiState, Beliefs, NavAgent, PatrolRoute>();

    public string Name => "AiAction";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var tick = time.Tick;

        ecsWorld.Query(in Guards, (Entity guard, ref Position position, ref AiState state,
            ref Beliefs beliefs, ref NavAgent nav, ref PatrolRoute route) =>
        {
            var guardTile = new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor);

            switch (state.Action)
            {
                case GuardAction.Patrol:
                    nav.SpeedTilesPerSecond = PatrolSpeed;
                    if (guardTile == route.Current)
                        route.Advance();
                    if (nav.Idle || nav.Destination != route.Current)
                        nav.SetDestination(guardTile, route.Current, pathfinding, priority: 30);
                    break;

                case GuardAction.Investigate:
                    if (state.InvestigateTarget is not { } spot)
                    {
                        state.Action = GuardAction.Patrol;
                        break;
                    }

                    nav.SpeedTilesPerSecond = InvestigateSpeed;
                    if (guardTile == spot)
                    {
                        if (state.InvestigateUntil == 0)
                        {
                            state.InvestigateUntil = tick + InvestigateDwellTicks;
                            nav.Clear();
                        }
                        else if (tick >= state.InvestigateUntil)
                        {
                            // Searched the spot, found nothing: resolve the stimulus and move on.
                            beliefs.UnresolvedSound = null;
                            state.InvestigateTarget = null;
                            state.InvestigateUntil = 0;
                            state.Action = GuardAction.Patrol;
                            state.DecisionRequested = true;
                        }
                    }
                    else
                    {
                        nav.SetDestination(guardTile, spot, pathfinding, priority: 70);
                    }

                    break;

                case GuardAction.Chase:
                case GuardAction.Arrest:
                    if (!state.HasChaseTarget
                        || !ecsWorld.IsAlive(state.ChaseTarget)
                        || !beliefs.Suspects.TryGetValue(state.ChaseTarget, out var belief))
                    {
                        state.Action = GuardAction.Patrol;
                        state.HasChaseTarget = false;
                        break;
                    }

                    nav.SpeedTilesPerSecond = ChaseSpeed;

                    if (state.Action == GuardAction.Arrest
                        && belief.CurrentlyVisible
                        && TilePos.EuclideanDistance(guardTile, belief.LastKnown) <= Reasoning.AiDecisionSystem.ArrestRange)
                    {
                        PerformArrest(ecsWorld, guard, state.ChaseTarget, guardTile, tick);
                        beliefs.Suspects.Remove(state.ChaseTarget);
                        state.Action = GuardAction.Patrol;
                        state.HasChaseTarget = false;
                        state.DecisionRequested = true;
                        nav.Clear();
                        break;
                    }

                    if (guardTile == belief.LastKnown && !belief.CurrentlyVisible)
                    {
                        // Reached the last known position and the suspect is not here:
                        // this belief is now weak — the decision layer will downgrade to a search.
                        belief.Confidence = MathF.Min(belief.Confidence, 0.3f);
                        state.DecisionRequested = true;
                        nav.Clear();
                    }
                    else
                    {
                        nav.SetDestination(guardTile, belief.LastKnown, pathfinding, priority: 90);
                    }

                    break;
            }
        });
    }

    private void PerformArrest(Arch.Core.World ecsWorld, Entity guard, Entity prisoner, TilePos where, ulong tick)
    {
        events.Publish(new ArrestEvent(tick, guard, prisoner, where));

        // Instant "escort to cell" (Phase 2 simplification).
        ref var position = ref ecsWorld.Get<Position>(prisoner);
        position = new Position(cellSpawn.X + 0.5f, cellSpawn.Y + 0.5f, cellSpawn.Floor);

        // Reset stride tracking so the teleport doesn't register as a burst of running noise.
        if (ecsWorld.Has<Footsteps>(prisoner))
            ecsWorld.Set(prisoner, new Footsteps());

        // Guards strip any disguise on arrest (contraband confiscation itself is a later phase).
        if (ecsWorld.Has<Items.Appearance>(prisoner))
            ecsWorld.Set(prisoner, new Items.Appearance(null));

        if (ecsWorld.Has<ThreatScore>(prisoner))
        {
            ref var threat = ref ecsWorld.Get<ThreatScore>(prisoner);
            // Observed history keeps them above a fresh inmate, but below chase threshold (§7.7).
            threat.Threat = MathF.Min(threat.Threat, 25f);
        }
    }
}
