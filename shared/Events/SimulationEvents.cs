using Arch.Core;
using Prison.Shared.World;

namespace Prison.Shared.Events;

/// <summary>Why a sound happened — data for telemetry and AI reasoning alike.</summary>
public enum SoundKind
{
    FootstepWalk,
    FootstepRun,
    Fighting,
    MetalCutting,
    Digging,
    Explosion,
}

/// <summary>
/// Every action emits a sound with a radius (PLAN §7.5). Entities within the radius get an
/// investigation stimulus — never an instant, certain detection.
/// </summary>
public readonly record struct SoundEmittedEvent(ulong Tick, TilePos Position, float RadiusTiles, SoundKind Kind, Entity Source);

/// <summary>
/// A guard physically saw a prisoner this perception update (PLAN §7.7 input signal).
/// Carries only what was *observed* — never hidden game state.
/// </summary>
public readonly record struct PrisonerObservedEvent(
    ulong Tick, Entity Guard, Entity Prisoner, TilePos Position,
    bool Running, bool InRestrictedZone, float ObservationSeconds);

/// <summary>A guard decided a visible prisoner is an escape threat and calls it in (PLAN §7.5 radio).</summary>
public readonly record struct SuspectAlertEvent(ulong Tick, Entity Guard, Entity Suspect, TilePos Position);

/// <summary>A radio alert arriving at the other guards after a realistic delay (PLAN §7.5).</summary>
public readonly record struct RadioBroadcastEvent(ulong Tick, Entity Suspect, TilePos LastKnownPosition, ulong SightingTick);

/// <summary>A guard caught a suspect.</summary>
public readonly record struct ArrestEvent(ulong Tick, Entity Guard, Entity Prisoner, TilePos Position);
