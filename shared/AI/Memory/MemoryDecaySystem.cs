using Arch.Core;
using Prison.Shared.ECS;

namespace Prison.Shared.AI.Memory;

/// <summary>
/// Belief confidence decays over time (PLAN §7.5: 100% → 75% → 50% → 20% → forgotten).
/// A guard never knows where a prisoner *is* — only where it last saw them, with fading
/// certainty. Old unresolved sounds are eventually dropped too.
/// </summary>
public sealed class MemoryDecaySystem : ISimulationSystem
{
    /// <summary>Confidence lost per second while the suspect is out of sight (~12s to forget).</summary>
    public const float DecayPerSecond = 0.08f;

    public const float ForgetBelow = 0.05f;

    /// <summary>Unresolved sounds older than this are dropped.</summary>
    public const float SoundMemorySeconds = 15f;

    private static readonly QueryDescription Rememberers = new QueryDescription().WithAll<Beliefs>();

    private readonly List<Entity> _forgotten = [];

    public string Name => "MemoryDecay";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        var dt = time.DeltaSeconds;
        var tick = time.Tick;
        var forgotten = _forgotten;

        ecsWorld.Query(in Rememberers, (ref Beliefs beliefs) =>
        {
            forgotten.Clear();
            foreach (var (suspect, belief) in beliefs.Suspects)
            {
                if (belief.CurrentlyVisible)
                    continue;
                belief.Confidence -= DecayPerSecond * dt;
                if (belief.Confidence < ForgetBelow)
                    forgotten.Add(suspect);
            }

            foreach (var suspect in forgotten)
                beliefs.Suspects.Remove(suspect);

            if (beliefs.UnresolvedSound is { } sound &&
                (tick - sound.Tick) * dt > SoundMemorySeconds)
            {
                beliefs.UnresolvedSound = null;
            }
        });
    }
}
