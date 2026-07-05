namespace Prison.Shared.Generation;

public enum SecurityLevel
{
    Low,
    Medium,
    High,
}

/// <summary>
/// The Design Intent (PLAN §8.3 step 1): family DNA/Doctrine resolved into the concrete
/// parameters one generation run consumes. Phase 5 keeps the parameter set small; Phase 6's
/// family system will produce these from heritable DNA/Doctrine data (§8.1).
/// </summary>
public sealed record DesignIntent
{
    public required int Seed { get; init; }

    /// <summary>Inmate capacity the prison must house.</summary>
    public int Capacity { get; init; } = 20;

    public SecurityLevel Security { get; init; } = SecurityLevel.Medium;

    /// <summary>Family DNA blueprint preferences (§8.1) — weighted higher during selection.</summary>
    public IReadOnlyList<string> PreferredBlueprints { get; init; } = [];

    // ---- Warden Doctrine counter-measures (§8.1/§9.2), applied on top of the security level ----

    public int ExtraPatrolGuards { get; init; }

    public int ExtraGuardStations { get; init; }

    /// <summary>Perimeter fence rings (1–3).</summary>
    public int FenceLayers { get; init; } = 1;

    /// <summary>0–1: share of the diggable dirt strip hardened into concrete.</summary>
    public float HardenedGroundBias { get; init; }

    /// <summary>Adds a patrol route hugging the inside of the innermost fence.</summary>
    public bool PerimeterPatrol { get; init; }

    /// <summary>Blueprint-provided uniform items are not placed in the world.</summary>
    public bool RestrictedUniformAccess { get; init; }

    /// <summary>Extra patrol guards beyond the ones each guard station provides.</summary>
    public int PatrolGuards => ExtraPatrolGuards + Security switch
    {
        SecurityLevel.Low => 1,
        SecurityLevel.Medium => 2,
        _ => 4,
    };

    public int GuardStations => ExtraGuardStations + (Security == SecurityLevel.High ? 2 : 1);

    /// <summary>Room types every believable prison needs regardless of doctrine (§8.4.B).</summary>
    public static readonly string[] RequiredRoomTypes =
        ["cafeteria", "kitchen", "medical", "workshop", "laundry", "showers", "visitation", "admin"];
}
