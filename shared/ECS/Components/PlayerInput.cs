namespace Prison.Shared.ECS.Components;

/// <summary>
/// The player's current input intent, written by the host (client input handler or, later,
/// the server's network message decoder) and consumed by simulation systems. Keeping input
/// as a component keeps the simulation authoritative and host-agnostic (PLAN §7.11).
/// </summary>
public record struct PlayerInput
{
    /// <summary>Desired movement direction, each axis in [-1, 1].</summary>
    public float MoveX;
    public float MoveY;

    /// <summary>Sprinting: faster, but louder and visibly suspicious (PLAN §7.5/§7.7).</summary>
    public bool Running;

    /// <summary>Set for one tick when the player asks to take the stairs they stand on.</summary>
    public bool UseStairs;
}
