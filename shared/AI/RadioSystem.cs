using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.Events;

namespace Prison.Shared.AI;

/// <summary>
/// Guards have no hive mind (PLAN §7.5): a sighting must be *reported* and arrives at the
/// other guards with a realistic delay. Radio-delivered knowledge is second-hand — it seeds
/// a belief with reduced confidence, never certain sight.
/// Later phases tie the delay to infrastructure state (destroyed radio tower ⇒ longer delay).
/// </summary>
public sealed class RadioSystem : ISimulationSystem
{
    /// <summary>Baseline transmission delay (PLAN §7.5: "a few seconds, tunable").</summary>
    public const float DelaySeconds = 3f;

    /// <summary>Second-hand information confidence (vs. 1.0 for direct sight).</summary>
    public const float RelayedConfidence = 0.6f;

    private static readonly QueryDescription Guards =
        new QueryDescription().WithAll<GuardTag, AiState, Beliefs>();

    private readonly EventBus _events;
    private readonly Queue<SuspectAlertEvent> _inFlight = [];

    public RadioSystem(EventBus events)
    {
        _events = events;
        events.Subscribe<SuspectAlertEvent>(_inFlight.Enqueue);
    }

    public string Name => "Radio";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var delayTicks = (ulong)(DelaySeconds / time.DeltaSeconds);

        while (_inFlight.TryPeek(out var alert) && time.Tick >= alert.Tick + delayTicks)
        {
            _inFlight.Dequeue();
            _events.Publish(new RadioBroadcastEvent(time.Tick, alert.Suspect, alert.Position, alert.Tick));

            ecsWorld.Query(in Guards, (Entity guard, ref AiState state, ref Beliefs beliefs) =>
            {
                if (guard == alert.Guard)
                    return;

                // Never downgrade fresher or equally-good existing knowledge with a stale relay —
                // but a guard with no belief at all always accepts the broadcast.
                if (beliefs.Suspects.TryGetValue(alert.Suspect, out var belief))
                {
                    if (belief.CurrentlyVisible ||
                        (belief.LastSeenTick >= alert.Tick && belief.Confidence >= RelayedConfidence))
                        return;
                }
                else
                {
                    beliefs.Suspects[alert.Suspect] = belief = new SuspectBelief();
                }

                belief.LastKnown = alert.Position;
                belief.LastSeenTick = alert.Tick;
                belief.Confidence = MathF.Max(belief.Confidence, RelayedConfidence);
                state.DecisionRequested = true;
            });
        }
    }
}
