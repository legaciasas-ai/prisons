using Arch.Core;
using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.World;

namespace Prison.Shared.AI.Perception;

/// <summary>
/// Staff hearing (PLAN §7.5): a sound within earshot generates an *investigation stimulus*,
/// not an instant certain detection. Guards ignore staff-sourced sounds (a colleague's
/// footsteps are identifiable).
/// Phase 2 simplification (documented): walls do not yet attenuate sound — per-tile
/// sound_transmission data exists and will be honoured when the sound layer matures.
/// </summary>
public sealed class HearingSystem : ISimulationSystem
{
    private static readonly QueryDescription Guards =
        new QueryDescription().WithAll<GuardTag, Position, AiState, Beliefs>();

    private readonly List<SoundEmittedEvent> _soundsThisTick = [];

    public HearingSystem(EventBus events)
    {
        events.Subscribe<SoundEmittedEvent>(_soundsThisTick.Add);
    }

    public string Name => "Hearing";

    public void Update(Arch.Core.World ecsWorld, in SimTime time)
    {
        if (_soundsThisTick.Count == 0)
            return;

        foreach (var sound in _soundsThisTick)
        {
            if (ecsWorld.IsAlive(sound.Source) && ecsWorld.Has<GuardTag>(sound.Source))
                continue;

            ecsWorld.Query(in Guards, (Entity guard, ref Position position, ref AiState state, ref Beliefs beliefs) =>
            {
                if (guard == sound.Source || position.Floor != sound.Position.Floor)
                    return;

                var listener = new TilePos((int)MathF.Floor(position.X), (int)MathF.Floor(position.Y), position.Floor);
                if (TilePos.EuclideanDistance(listener, sound.Position) > sound.RadiusTiles)
                    return;

                beliefs.UnresolvedSound = new SoundStimulus(sound.Position, sound.Tick, sound.RadiusTiles);
                state.DecisionRequested = true;
            });
        }

        _soundsThisTick.Clear();
    }
}
