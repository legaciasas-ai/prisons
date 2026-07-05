using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Prison.Shared.ECS;

namespace Prison.Shared;

// Aliased because the sibling Prison.Shared.World namespace shadows the Arch type name here.
using EcsWorld = Arch.Core.World;

/// <summary>
/// The authoritative Core Simulation (PLAN §4, Pillar #3 and #5): identical on the Godot client,
/// the headless dedicated server, and player-hosted servers. Runs at a fixed tick rate,
/// fully decoupled from render framerate; hosts feed it wall-clock time via <see cref="Advance"/>.
/// </summary>
public sealed class Simulation : IDisposable
{
    public const int DefaultTicksPerSecond = 20;

    private readonly List<ISimulationSystem> _systems = [];
    private readonly ILogger _logger;
    private double _accumulator;

    public Simulation(int ticksPerSecond = DefaultTicksPerSecond, ILogger<Simulation>? logger = null)
    {
        if (ticksPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));

        TicksPerSecond = ticksPerSecond;
        FixedDeltaSeconds = 1f / ticksPerSecond;
        _logger = logger ?? NullLogger<Simulation>.Instance;
        World = EcsWorld.Create();
    }

    public EcsWorld World { get; }

    /// <summary>The in-simulation event bus (PLAN §7.9), shared by all systems.</summary>
    public Events.EventBus Events { get; } = new();

    public int TicksPerSecond { get; }

    public float FixedDeltaSeconds { get; }

    public ulong CurrentTick { get; private set; }

    /// <summary>Restores the tick counter when loading a save (PLAN §7.12).</summary>
    public void RestoreTick(ulong tick) => CurrentTick = tick;

    public SimTime Time => new(CurrentTick, FixedDeltaSeconds);

    /// <summary>
    /// If a host stalls (debugger pause, machine sleep), never try to catch up more than this many
    /// seconds of simulated time in one call — drop the excess instead of spiraling.
    /// </summary>
    public double MaxCatchUpSeconds { get; init; } = 1.0;

    public IReadOnlyList<ISimulationSystem> Systems => _systems;

    public void AddSystem(ISimulationSystem system)
    {
        _systems.Add(system);
        _logger.LogDebug("System registered: {System}", system.Name);
    }

    /// <summary>Runs exactly one fixed simulation step.</summary>
    public void Tick()
    {
        var time = new SimTime(CurrentTick, FixedDeltaSeconds);
        foreach (var system in _systems)
            system.Update(World, in time);
        CurrentTick++;
    }

    /// <summary>
    /// Feeds elapsed wall-clock seconds into the fixed-step accumulator and runs as many
    /// ticks as are due. Returns the number of ticks executed.
    /// </summary>
    public int Advance(double elapsedSeconds)
    {
        if (elapsedSeconds < 0)
            elapsedSeconds = 0;

        if (elapsedSeconds > MaxCatchUpSeconds)
        {
            _logger.LogWarning(
                "Simulation fell behind by {Elapsed:F2}s; clamping catch-up to {Max:F2}s",
                elapsedSeconds, MaxCatchUpSeconds);
            elapsedSeconds = MaxCatchUpSeconds;
        }

        _accumulator += elapsedSeconds;

        // 1ns tolerance so that e.g. 0.25s at 20 ticks/s reliably yields 5 ticks despite
        // binary floating-point rounding of the fixed step.
        const double epsilon = 1e-9;
        var step = 1.0 / TicksPerSecond;

        var ticksRun = 0;
        while (_accumulator >= step - epsilon)
        {
            _accumulator -= step;
            Tick();
            ticksRun++;
        }

        return ticksRun;
    }

    public void Dispose() => World.Dispose();
}
