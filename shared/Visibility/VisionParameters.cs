namespace Prison.Shared.Visibility;

/// <summary>
/// Parameters for one field-of-view computation. Players, guards, cameras, dogs and towers
/// all use the same module with different parameters — there is no separate "AI vision"
/// (Pillar #2, PLAN §7.4).
/// </summary>
public readonly record struct VisionParameters
{
    /// <summary>Max sight distance in tiles, in fully lit conditions.</summary>
    public float MaxDistance { get; init; }

    /// <summary>
    /// Sight distance in complete darkness. Lighting interpolates the effective range between
    /// this and <see cref="MaxDistance"/> (PLAN §7.4: lighting limits how far a clear line of
    /// sight can resolve detail; it never creates sight through walls).
    /// </summary>
    public float DarkDistance { get; init; }

    /// <summary>Field-of-view cone in degrees; 360 = omnidirectional (player top-down view).</summary>
    public float FovDegrees { get; init; }

    /// <summary>Facing angle in radians (ignored for 360° vision).</summary>
    public float FacingRadians { get; init; }

    public static VisionParameters Omnidirectional(float maxDistance, float darkDistance) => new()
    {
        MaxDistance = maxDistance,
        DarkDistance = darkDistance,
        FovDegrees = 360f,
        FacingRadians = 0f,
    };

    /// <summary>Guard-style vision cone (PLAN §7.5: ~120° default, tunable).</summary>
    public static VisionParameters Cone(float maxDistance, float darkDistance, float facingRadians, float fovDegrees = 120f) => new()
    {
        MaxDistance = maxDistance,
        DarkDistance = darkDistance,
        FovDegrees = fovDegrees,
        FacingRadians = facingRadians,
    };
}
