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
    /// <summary>A thrown object landing — the classic diversion.</summary>
    Impact,
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

// ---- Escapist mechanics (PLAN §7.8): every mechanic reports itself on the bus so the
// ---- Staff AI, telemetry and heat maps react to the same signals without hard wiring.

public readonly record struct ItemPickedUpEvent(ulong Tick, Entity Actor, string ItemId, TilePos Position);

public readonly record struct ItemCraftedEvent(ulong Tick, Entity Actor, string RecipeId, string OutputItemId);

/// <summary>A diggable floor tile was tunnelled through.</summary>
public readonly record struct TileDugEvent(ulong Tick, Entity Actor, TilePos Position);

/// <summary>A cuttable wall tile (fence) was cut open.</summary>
public readonly record struct FenceCutEvent(ulong Tick, Entity Actor, TilePos Position);

public readonly record struct DoorUnlockedEvent(ulong Tick, Entity Actor, TilePos Position);

public readonly record struct DoorToggledEvent(ulong Tick, Entity Actor, TilePos Position, bool Open);

/// <summary>An entity donned (Role != null) or removed (Role == null) a disguise.</summary>
public readonly record struct DisguiseChangedEvent(ulong Tick, Entity Actor, string? Role);

/// <summary>A guard got close enough to see through a prisoner's disguise (PLAN §7.8).</summary>
public readonly record struct DisguiseCompromisedEvent(ulong Tick, Entity Guard, Entity Prisoner, TilePos Position);

/// <summary>An item was thrown to create noise somewhere else (diversion).</summary>
public readonly record struct DiversionEvent(ulong Tick, Entity Actor, string ItemId, TilePos Target);

/// <summary>
/// A prisoner physically made it out of the compound (reached the map border). The signal
/// that marks a prison Compromised (§10.2) and closes an escape record for the Evolution
/// pipeline (§9.1). Fired once per prisoner per match.
/// </summary>
public readonly record struct EscapeSucceededEvent(ulong Tick, Entity Prisoner, TilePos Position);
