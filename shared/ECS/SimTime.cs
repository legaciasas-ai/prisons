namespace Prison.Shared.ECS;

/// <summary>
/// The simulation clock passed to every system on every tick.
/// The simulation runs at a fixed tick rate, decoupled from render framerate (PLAN §7.11).
/// </summary>
public readonly record struct SimTime(ulong Tick, float DeltaSeconds)
{
    /// <summary>Total simulated time elapsed since tick 0.</summary>
    public double TotalSeconds => Tick * (double)DeltaSeconds;
}
