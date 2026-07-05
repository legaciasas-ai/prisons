namespace Prison.Shared.AI.Scheduling;

/// <summary>
/// Simulation Level of Detail (PLAN §7.10): how much computational precision an NPC gets,
/// based on relevance. Only *precision* scales — never the rules (Pillar #3/§12.1).
/// </summary>
public enum SimulationLod : byte
{
    /// <summary>A prisoner is close (or the guard is engaged): full-rate perception and decisions.</summary>
    Full = 0,

    /// <summary>Same floor, not directly observed: reduced perception rate.</summary>
    Reduced = 1,

    /// <summary>Other side of the prison: patrol-focused, coarse update rates.</summary>
    Coarse = 2,

    /// <summary>No prisoner on this floor: no perception/pathfinding — discrete waypoint hops.</summary>
    EventOnly = 3,

    /// <summary>No prisoners at all (empty wing/server): statistical schedule-following only.</summary>
    Statistical = 4,
}

/// <summary>
/// The one computational budget behind every hosting tier (PLAN §12.1): profiles are presets
/// over these knobs, and the auto-tuner (<see cref="AiBudgetAutoTuner"/>) moves the *dynamic*
/// knobs at runtime. Gameplay rules never live here — only precision/update rates.
/// </summary>
public sealed class SimulationBudget
{
    public required string ProfileName { get; init; }

    // ---- structural knobs (fixed per profile) ----

    /// <summary>Within this distance of a prisoner (same floor), a guard runs at LOD Full.</summary>
    public float FullDetailRadiusTiles { get; init; } = 12f;

    /// <summary>Within this distance (same floor), LOD Reduced; beyond it, Coarse.</summary>
    public float ReducedDetailRadiusTiles { get; init; } = 28f;

    /// <summary>Pathfinding worker budget per tick at rest (PLAN §7.3 shared queue).</summary>
    public int BasePathfindingBudgetPerTick { get; init; } = 4;

    /// <summary>Decision heartbeat at LOD Full when nothing triggers a decision (§7.6).</summary>
    public uint BaseDecisionHeartbeatTicks { get; init; } = 20;

    /// <summary>Cadence of discrete patrol resolution at LOD EventOnly/Statistical
    /// ("guard reached cafeteria, instantly" — §7.10 LOD 3/4).</summary>
    public uint EventOnlyHopTicks { get; init; } = 40;

    // ---- dynamic knobs (moved by the auto-tuner; start at their base values) ----

    /// <summary>Current pathfinding requests served per tick.</summary>
    public int PathfindingBudgetPerTick { get; set; }

    /// <summary>
    /// ≥ 1. Degradation multiplier applied to *non-Full* update intervals, so overload
    /// starves the low-priority end first (§7.10) — an engaged, player-adjacent guard stays
    /// crisp while far-away patrols slow down.
    /// </summary>
    public float DegradationScale { get; set; } = 1f;

    // ---- profiles (PLAN §12.1) — fresh instances: budgets are mutable at runtime ----

    public static SimulationBudget Lightweight => new()
    {
        ProfileName = "Lightweight",
        FullDetailRadiusTiles = 10f,
        ReducedDetailRadiusTiles = 20f,
        BasePathfindingBudgetPerTick = 2,
        EventOnlyHopTicks = 60,
        PathfindingBudgetPerTick = 2,
    };

    public static SimulationBudget Balanced => new()
    {
        ProfileName = "Balanced",
        PathfindingBudgetPerTick = 4,
    };

    public static SimulationBudget HighCapacity => new()
    {
        ProfileName = "HighCapacity",
        FullDetailRadiusTiles = 14f,
        ReducedDetailRadiusTiles = 36f,
        BasePathfindingBudgetPerTick = 8,
        EventOnlyHopTicks = 30,
        PathfindingBudgetPerTick = 8,
    };

    public static SimulationBudget Dedicated => new()
    {
        ProfileName = "Dedicated",
        FullDetailRadiusTiles = 16f,
        ReducedDetailRadiusTiles = 44f,
        BasePathfindingBudgetPerTick = 12,
        EventOnlyHopTicks = 20,
        PathfindingBudgetPerTick = 12,
    };

    /// <summary>Case-insensitive profile lookup; unknown names fall back to Balanced.</summary>
    public static SimulationBudget ForProfile(string? name) => name?.ToLowerInvariant() switch
    {
        "lightweight" => Lightweight,
        "highcapacity" => HighCapacity,
        "dedicated" => Dedicated,
        _ => Balanced,
    };

    // ---- interval computation (pure — the scheduler math lives here, testable) ----

    /// <summary>Base perception interval in ticks per activity (PLAN §7.10: chasing updates far more often).</summary>
    public static uint BasePerceptionIntervalTicks(GuardAction action) => action switch
    {
        GuardAction.Chase or GuardAction.Arrest => 2,
        GuardAction.Investigate => 5,
        _ => 10,
    };

    private static float LodFactor(SimulationLod lod) => lod switch
    {
        SimulationLod.Full => 1f,
        SimulationLod.Reduced => 2f,
        _ => 5f,
    };

    /// <summary>
    /// Effective perception interval for a guard. LOD Full is exempt from degradation:
    /// under load, far-away guards slow down long before anyone the player can see does.
    /// Callers must skip perception entirely at LOD EventOnly/Statistical.
    /// </summary>
    public uint PerceptionIntervalTicks(GuardAction action, SimulationLod lod)
    {
        var scale = lod == SimulationLod.Full ? 1f : DegradationScale;
        return (uint)MathF.Max(1f, BasePerceptionIntervalTicks(action) * LodFactor(lod) * scale);
    }

    /// <summary>Effective decision heartbeat. Triggered decisions (sighting, sound, radio)
    /// always run immediately regardless of LOD — this only paces the idle rethink.</summary>
    public uint DecisionHeartbeatTicks(SimulationLod lod)
    {
        var scale = lod == SimulationLod.Full ? 1f : DegradationScale;
        return (uint)MathF.Max(1f, BaseDecisionHeartbeatTicks * LodFactor(lod) * scale);
    }
}
