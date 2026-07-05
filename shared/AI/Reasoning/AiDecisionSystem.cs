using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.World;

namespace Prison.Shared.AI.Reasoning;

/// <summary>
/// Utility AI decision-making (PLAN §7.6): every candidate action gets a numeric score and
/// the highest wins. Runs on triggers (sightings, sounds, radio) plus a slow heartbeat —
/// never every tick (§7.10). Scores are seeded from the plan's reference numbers
/// (patrol 12, investigate 55, chase 91, arrest 100).
/// </summary>
public sealed class AiDecisionSystem(EventBus events) : ISimulationSystem
{
    public const float PatrolScore = 12f;
    public const float InvestigateSoundScore = 55f;
    public const float InvestigateLostSuspectScore = 40f;
    public const float ChaseVisibleScore = 91f;
    public const float ChaseLastKnownScore = 70f;
    public const float ArrestScore = 100f;

    /// <summary>Arm's reach for an arrest, in tiles.</summary>
    public const float ArrestRange = 1.6f;

    /// <summary>Decision heartbeat when nothing triggers one (1s at 20 ticks/s).</summary>
    public const uint HeartbeatTicks = 20;

    private static readonly QueryDescription Guards =
        new QueryDescription().WithAll<GuardTag, Position, AiState, Beliefs>();

    public string Name => "AiDecision";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var tick = time.Tick;
        var dt = time.DeltaSeconds;

        ecsWorld.Query(in Guards, (Entity guard, ref Position position, ref AiState state, ref Beliefs beliefs) =>
        {
            if (!state.DecisionRequested && tick < state.NextDecisionTick)
                return;
            state.DecisionRequested = false;
            state.NextDecisionTick = tick + HeartbeatTicks;

            var guardTile = new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor);

            // --- Score: investigate an unresolved sound (freshness-weighted). ---
            var investigate = 0f;
            TilePos? investigateTarget = null;
            if (beliefs.UnresolvedSound is { } sound)
            {
                var age = (tick - sound.Tick) * dt;
                var freshness = Math.Clamp(1f - age / Memory.MemoryDecaySystem.SoundMemorySeconds, 0f, 1f);
                investigate = InvestigateSoundScore * freshness;
                investigateTarget = sound.Position;
            }

            // --- Score: chase / arrest a threatening suspect; investigate a lost one. ---
            var chase = 0f;
            var arrest = 0f;
            Entity bestSuspect = default;
            var hasSuspect = false;

            foreach (var (suspect, belief) in beliefs.Suspects)
            {
                if (!ecsWorld.IsAlive(suspect) || !ecsWorld.Has<ThreatScore>(suspect))
                    continue;
                var threat = ecsWorld.Get<ThreatScore>(suspect).Threat;

                if (threat >= ThreatScore.ChaseThreshold)
                {
                    if (belief.CurrentlyVisible)
                    {
                        var distance = TilePos.EuclideanDistance(guardTile, belief.LastKnown);
                        var score = distance <= ArrestRange ? ArrestScore : ChaseVisibleScore;
                        if (score > MathF.Max(chase, arrest))
                        {
                            bestSuspect = suspect;
                            hasSuspect = true;
                            if (distance <= ArrestRange)
                                arrest = score;
                            else
                                chase = score;
                        }
                    }
                    else if (belief.Confidence >= 0.5f && chase < ChaseLastKnownScore && arrest == 0f)
                    {
                        chase = ChaseLastKnownScore;
                        bestSuspect = suspect;
                        hasSuspect = true;
                    }
                }
                else if (!belief.CurrentlyVisible && belief.Confidence is >= 0.3f and < 1f && threat >= 25f)
                {
                    // Mildly suspicious person slipped out of sight: worth a look at the last known spot.
                    var score = InvestigateLostSuspectScore * belief.Confidence;
                    if (score > investigate)
                    {
                        investigate = score;
                        investigateTarget = belief.LastKnown;
                    }
                }
            }

            // --- Choose the highest-scoring action (PLAN §7.6). ---
            var previousAction = state.Action;
            var previousTarget = state.ChaseTarget;

            if (arrest > 0f && arrest >= chase && arrest >= investigate && arrest >= PatrolScore)
            {
                state.Action = GuardAction.Arrest;
                state.ChaseTarget = bestSuspect;
                state.HasChaseTarget = true;
            }
            else if (chase > investigate && chase > PatrolScore && hasSuspect)
            {
                state.Action = GuardAction.Chase;
                state.ChaseTarget = bestSuspect;
                state.HasChaseTarget = true;

                // First-hand sighting of a new chase: call it in on the radio (PLAN §7.5).
                var isNewPursuit = previousAction is not (GuardAction.Chase or GuardAction.Arrest)
                    || previousTarget != bestSuspect;
                if (isNewPursuit && beliefs.Suspects[bestSuspect].CurrentlyVisible)
                {
                    events.Publish(new SuspectAlertEvent(
                        tick, guard, bestSuspect, beliefs.Suspects[bestSuspect].LastKnown));
                }
            }
            else if (investigate > PatrolScore && investigateTarget is not null)
            {
                if (state.Action != GuardAction.Investigate || state.InvestigateTarget != investigateTarget)
                {
                    state.Action = GuardAction.Investigate;
                    state.InvestigateTarget = investigateTarget;
                    state.InvestigateUntil = 0;
                }

                state.HasChaseTarget = false;
            }
            else
            {
                state.Action = GuardAction.Patrol;
                state.HasChaseTarget = false;
                state.InvestigateTarget = null;
            }
        });
    }
}
