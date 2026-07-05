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

    /// <summary>Extra patrol guards beyond the ones each guard station provides.</summary>
    public int PatrolGuards => Security switch
    {
        SecurityLevel.Low => 1,
        SecurityLevel.Medium => 2,
        _ => 4,
    };

    public int GuardStations => Security == SecurityLevel.High ? 2 : 1;

    /// <summary>Room types every believable prison needs regardless of doctrine (§8.4.B).</summary>
    public static readonly string[] RequiredRoomTypes =
        ["cafeteria", "kitchen", "medical", "workshop", "laundry", "showers", "visitation", "admin"];
}
