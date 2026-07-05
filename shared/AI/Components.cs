using Arch.Core;
using Prison.Shared.Pathfinding;
using Prison.Shared.World;

namespace Prison.Shared.AI;

/// <summary>Marks an entity as prison staff controlled by the Staff AI.</summary>
public record struct GuardTag;

/// <summary>Marks an entity as a prisoner (player-controlled or NPC) that staff watch over.</summary>
public record struct PrisonerTag;

/// <summary>Which way the entity's head points, radians (0 = +X). Drives vision cones.</summary>
public record struct Facing(float Radians);

/// <summary>
/// Per-entity vision sense parameters, resolved through the one shared Visibility module —
/// same raycasts as the player's fog of war, no separate "AI vision" (Pillar #2).
/// </summary>
public record struct VisionSense(float MaxDistance, float DarkDistance, float FovDegrees)
{
    /// <summary>PLAN §7.5 defaults: ~120° cone, tunable.</summary>
    public static VisionSense GuardDefault => new(MaxDistance: 10f, DarkDistance: 2.5f, FovDegrees: 120f);
}

/// <summary>
/// Tracks distance travelled to emit footstep sounds (PLAN §7.5 hearing table:
/// walking 2m, running 8m) and exposes the *observable* movement speed.
/// </summary>
public record struct Footsteps
{
    public const float StrideTiles = 0.8f;
    public const float RunSpeedThreshold = 4.5f;

    public float PrevX;
    public float PrevY;
    public bool HasPrev;
    public float DistanceAccumulator;

    /// <summary>Speed over the last tick, in tiles/second — what an observer could physically see.</summary>
    public float ObservableSpeed;
}

/// <summary>
/// A prisoner's threat score (PLAN §7.7), computed purely from *observed* signals —
/// never from hidden ground truth.
/// </summary>
public record struct ThreatScore(float Threat)
{
    /// <summary>Threat at/above which a guard treats a visible prisoner as an escape attempt.</summary>
    public const float ChaseThreshold = 60f;
}

/// <summary>A guard's patrol route (waypoints on the guard's floor).</summary>
public sealed class PatrolRoute
{
    public required TilePos[] Waypoints { get; init; }

    public int NextIndex { get; set; }

    public TilePos Current => Waypoints[NextIndex];

    public void Advance() => NextIndex = (NextIndex + 1) % Waypoints.Length;
}

public enum GuardAction
{
    Patrol,
    Investigate,
    Chase,
    Arrest,
}

/// <summary>
/// A guard's current decision state plus its AI-scheduler bookkeeping (PLAN §7.10 skeleton:
/// perception updates at a rate depending on importance; decisions run on triggers or a
/// slow heartbeat, not every tick).
/// </summary>
public sealed class AiState
{
    public GuardAction Action { get; set; } = GuardAction.Patrol;

    public Entity ChaseTarget { get; set; }
    public bool HasChaseTarget { get; set; }

    public TilePos? InvestigateTarget { get; set; }

    /// <summary>Tick until which the guard dwells and looks around at an investigation spot.</summary>
    public ulong InvestigateUntil { get; set; }

    public ulong NextPerceptionTick { get; set; }
    public ulong NextDecisionTick { get; set; }

    /// <summary>Set by perception/hearing/radio when something changed — triggers a decision now.</summary>
    public bool DecisionRequested { get; set; } = true;
}

/// <summary>
/// What a guard believes about one suspect: last seen where/when, with a confidence that
/// decays over time (PLAN §7.5 memory: belief, not omniscience).
/// </summary>
public sealed class SuspectBelief
{
    public TilePos LastKnown { get; set; }
    public ulong LastSeenTick { get; set; }
    public float Confidence { get; set; }
    public bool CurrentlyVisible { get; set; }

    /// <summary>This guard got close enough to recognize the suspect despite a disguise (fires once).</summary>
    public bool SeenThroughDisguise { get; set; }
}

/// <summary>
/// The simulation LOD assigned to this NPC by <c>LodSystem</c> (PLAN §7.10), plus its
/// scheduler bookkeeping. Not persisted in saves — recomputed within half a second.
/// </summary>
public record struct SimulationDetail(Scheduling.SimulationLod Lod)
{
    /// <summary>Next tick the LOD gets re-evaluated (staggered across entities).</summary>
    public ulong NextEvaluationTick;

    /// <summary>Next discrete waypoint hop when at LOD EventOnly/Statistical.</summary>
    public ulong NextEventTick;

    /// <summary>This entity's LOD, defaulting to Full for entities without the component.</summary>
    public static Scheduling.SimulationLod Of(Arch.Core.World ecs, Entity entity) =>
        ecs.Has<SimulationDetail>(entity) ? ecs.Get<SimulationDetail>(entity).Lod : Scheduling.SimulationLod.Full;
}

/// <summary>A sound the guard heard and has not yet resolved (investigation stimulus, PLAN §7.5).</summary>
public record struct SoundStimulus(TilePos Position, ulong Tick, float Radius);

/// <summary>A guard's memory: beliefs about suspects plus the latest unresolved sound.</summary>
public sealed class Beliefs
{
    public Dictionary<Entity, SuspectBelief> Suspects { get; } = [];

    public SoundStimulus? UnresolvedSound { get; set; }
}

/// <summary>
/// Navigation state: a path being followed, or a pending request in the shared pathfinding
/// queue (PLAN §7.3). Movement itself happens in <c>NavAgentSystem</c>.
/// </summary>
public sealed class NavAgent
{
    public List<TilePos>? Path { get; set; }
    public int NextIndex { get; set; }
    public PathRequest? Pending { get; set; }
    public TilePos? Destination { get; set; }

    /// <summary>Movement speed for the current activity (patrol amble vs. chase sprint).</summary>
    public float SpeedTilesPerSecond { get; set; } = 2.5f;

    public bool Idle => Path is null && Pending is null;

    public void SetDestination(TilePos from, TilePos destination, PathfindingService pathfinding, int priority)
    {
        if (Destination == destination && !Idle)
            return;

        Destination = destination;
        Path = null;
        NextIndex = 0;
        Pending = pathfinding.Request(from, destination, priority);
    }

    public void Clear()
    {
        Path = null;
        NextIndex = 0;
        Pending = null;
        Destination = null;
    }
}
