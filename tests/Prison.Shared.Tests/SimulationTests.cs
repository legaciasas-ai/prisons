using Prison.Shared.ECS;
using Prison.Shared.ECS.Components;
using Prison.Shared.ECS.Systems;
using Xunit;

namespace Prison.Shared.Tests;

public class SimulationTests
{
    [Fact]
    public void Advance_RunsExpectedNumberOfFixedTicks()
    {
        using var sim = new Simulation(ticksPerSecond: 20);

        var ticks = sim.Advance(0.25);

        Assert.Equal(5, ticks);
        Assert.Equal(5ul, sim.CurrentTick);
    }

    [Fact]
    public void Advance_AccumulatesFractionalTime()
    {
        using var sim = new Simulation(ticksPerSecond: 20);

        Assert.Equal(0, sim.Advance(0.03)); // 30ms < 50ms tick: nothing due yet
        Assert.Equal(1, sim.Advance(0.03)); // 60ms accumulated: one tick due
        Assert.Equal(1ul, sim.CurrentTick);
    }

    [Fact]
    public void Advance_ClampsCatchUpAfterLongStall()
    {
        using var sim = new Simulation(ticksPerSecond: 20) { MaxCatchUpSeconds = 1.0 };

        var ticks = sim.Advance(60.0); // e.g. machine slept — must not run 1200 ticks

        Assert.Equal(20, ticks);
    }

    [Fact]
    public void Advance_NegativeElapsedIsIgnored()
    {
        using var sim = new Simulation(ticksPerSecond: 20);

        Assert.Equal(0, sim.Advance(-1.0));
        Assert.Equal(0ul, sim.CurrentTick);
    }

    [Fact]
    public void MovementSystem_IntegratesVelocityPerFixedStep()
    {
        using var sim = new Simulation(ticksPerSecond: 20);
        sim.AddSystem(new MovementSystem());
        var entity = sim.World.Create(new Position(0f, 0f, Floor: 0), new Velocity(2f, -1f));

        for (var i = 0; i < 20; i++) // exactly one simulated second
            sim.Tick();

        var position = sim.World.Get<Position>(entity);
        Assert.Equal(2f, position.X, precision: 4);
        Assert.Equal(-1f, position.Y, precision: 4);
        Assert.Equal(0, position.Floor);
    }

    [Fact]
    public void Tick_AdvancesTimeAtFixedRate()
    {
        using var sim = new Simulation(ticksPerSecond: 20);
        SimTime? observed = null;
        sim.AddSystem(new ProbeSystem(t => observed = t));

        sim.Tick();
        sim.Tick();

        Assert.NotNull(observed);
        Assert.Equal(1ul, observed.Value.Tick);
        Assert.Equal(0.05f, observed.Value.DeltaSeconds, precision: 6);
        Assert.Equal(0.05, observed.Value.TotalSeconds, precision: 6);
    }

    private sealed class ProbeSystem(Action<SimTime> onUpdate) : ISimulationSystem
    {
        public string Name => "Probe";

        public void Update(Arch.Core.World world, in SimTime time) => onUpdate(time);
    }
}
