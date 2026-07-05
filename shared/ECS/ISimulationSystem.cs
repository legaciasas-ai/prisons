namespace Prison.Shared.ECS;

// Declared inside the namespace so the alias beats the sibling Prison.Shared.World namespace.
using World = Arch.Core.World;

/// <summary>
/// A simulation system: the only kind of code allowed to read/write components (PLAN §7.1).
/// Systems receive their dependencies via constructor injection (PLAN §15) and communicate
/// with each other only through components and events, never direct references.
/// </summary>
public interface ISimulationSystem
{
    /// <summary>Stable name used for logging, profiling and telemetry.</summary>
    string Name { get; }

    /// <summary>Runs one fixed simulation step over the world.</summary>
    void Update(World world, in SimTime time);
}
