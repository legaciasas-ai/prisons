using Arch.Core;
using Prison.Shared.AI;
using Prison.Shared.ECS;
using Prison.Shared.Events;

namespace Prison.Shared.Suspicion;

/// <summary>
/// Maintains each prisoner's threat score purely from observed signals (PLAN §7.7):
/// being *seen* in a restricted area or running raises it; time lowers it slowly.
/// Nothing here reads hidden state — every input is a physically-witnessed observation event.
/// </summary>
public sealed class SuspicionSystem : ISimulationSystem
{
    public const float RestrictedZonePerSecond = 40f;
    public const float RunningPerSecond = 15f;
    public const float DecayPerSecond = 2f;
    public const float MaxThreat = 100f;

    /// <summary>Being caught in a disguise is damning on its own (PLAN §7.7 observed signal).</summary>
    public const float DisguiseCompromisedBoost = 55f;

    private static readonly QueryDescription Prisoners =
        new QueryDescription().WithAll<PrisonerTag, ThreatScore>();

    private readonly List<PrisonerObservedEvent> _observations = [];
    private readonly List<DisguiseCompromisedEvent> _compromises = [];

    public SuspicionSystem(EventBus events)
    {
        events.Subscribe<PrisonerObservedEvent>(_observations.Add);
        events.Subscribe<DisguiseCompromisedEvent>(_compromises.Add);
    }

    public string Name => "Suspicion";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        foreach (var observation in _observations)
        {
            if (!ecsWorld.IsAlive(observation.Prisoner) || !ecsWorld.Has<ThreatScore>(observation.Prisoner))
                continue;

            ref var score = ref ecsWorld.Get<ThreatScore>(observation.Prisoner);
            var delta = 0f;
            if (observation.InRestrictedZone)
                delta += RestrictedZonePerSecond * observation.ObservationSeconds;
            if (observation.Running)
                delta += RunningPerSecond * observation.ObservationSeconds;

            score.Threat = MathF.Min(MaxThreat, score.Threat + delta);
        }

        _observations.Clear();

        foreach (var compromise in _compromises)
        {
            if (!ecsWorld.IsAlive(compromise.Prisoner) || !ecsWorld.Has<ThreatScore>(compromise.Prisoner))
                continue;
            ref var score = ref ecsWorld.Get<ThreatScore>(compromise.Prisoner);
            score.Threat = MathF.Min(MaxThreat, score.Threat + DisguiseCompromisedBoost);
        }

        _compromises.Clear();

        // Suspicion cools off slowly with time (never below zero).
        var dt = time.DeltaSeconds;
        ecsWorld.Query(in Prisoners, (ref ThreatScore score) =>
        {
            score.Threat = MathF.Max(0f, score.Threat - DecayPerSecond * dt);
        });
    }
}
