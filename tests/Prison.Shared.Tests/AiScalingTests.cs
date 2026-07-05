using Arch.Core;
using Prison.Shared.AI;
using Prison.Shared.AI.Scheduling;
using Prison.Shared.ECS.Components;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>
/// Phase 9 (PLAN §7.10/§12): simulation LOD, priority-based scheduling, the dynamic AI
/// budget, and graceful degradation — precision scales, rules never do.
/// </summary>
[Collection("content")]
public class AiScalingTests(TestContent content)
{
    // ---------- helpers ----------

    private MatchHandle NewMatch(WorldGrid world, SimulationBudget? budget = null) =>
        MatchFactory.Create(world, content.Map, content.Items, content.Recipes,
            includeMapGuards: false, budget: budget);

    private static Entity SpawnGuard(Simulation sim, float x, float y, int floor)
    {
        var guard = MatchFactory.SpawnGuard(sim, new MapDefinition.MapGuard
        {
            Floor = floor, X = (int)x, Y = (int)y, Patrol = [[(int)x, (int)y], [(int)x + 2, (int)y]],
        });
        sim.World.Get<Position>(guard) = new Position(x, y, floor);
        return guard;
    }

    private static void RunTicks(Simulation sim, int ticks)
    {
        for (var i = 0; i < ticks; i++)
            sim.Tick();
    }

    private static SimulationLod LodOf(Simulation sim, Entity guard) =>
        sim.World.Get<SimulationDetail>(guard).Lod;

    // ---------- profiles (§12.1) ----------

    [Fact]
    public void Profiles_ScalePrecisionKnobs_NotRules()
    {
        var lightweight = SimulationBudget.Lightweight;
        var dedicated = SimulationBudget.Dedicated;

        Assert.True(lightweight.PathfindingBudgetPerTick < dedicated.PathfindingBudgetPerTick);
        Assert.True(lightweight.ReducedDetailRadiusTiles < dedicated.ReducedDetailRadiusTiles);
        Assert.True(lightweight.EventOnlyHopTicks > dedicated.EventOnlyHopTicks);

        // Unknown/legacy names fall back to Balanced instead of crashing a host.
        Assert.Equal("Balanced", SimulationBudget.ForProfile("???").ProfileName);
        Assert.Equal("Dedicated", SimulationBudget.ForProfile("dedicated").ProfileName);
    }

    // ---------- LOD assignment (§7.10) ----------

    [Fact]
    public void Lod_FollowsDistanceFloorAndEngagement()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;

        // Player sits at a known spot on floor 0.
        sim.World.Get<Position>(match.Player) = new Position(5.5f, 7.5f, 0);

        var near = SpawnGuard(sim, 10.5f, 7.5f, 0);      // ~5 tiles away, same floor
        var far = SpawnGuard(sim, 36.5f, 20.5f, 0);      // opposite corner, same floor
        var upstairs = SpawnGuard(sim, 20.5f, 5.5f, 1);  // no prisoner on floor 1

        RunTicks(sim, (int)LodSystem.EvaluationIntervalTicks * 2 + 1);

        Assert.Equal(SimulationLod.Full, LodOf(sim, near));
        Assert.True(LodOf(sim, far) >= SimulationLod.Reduced, "a far guard must not run at full detail");
        Assert.Equal(SimulationLod.EventOnly, LodOf(sim, upstairs));

        // Engagement overrides distance: the far guard starts chasing → promoted to Full.
        var state = sim.World.Get<AiState>(far);
        state.Action = GuardAction.Chase;
        state.ChaseTarget = match.Player;
        state.HasChaseTarget = true;
        sim.World.Get<Beliefs>(far).Suspects[match.Player] = new SuspectBelief
        {
            LastKnown = new TilePos(5, 7, 0), Confidence = 1f, CurrentlyVisible = true,
        };
        RunTicks(sim, (int)LodSystem.EvaluationIntervalTicks * 2 + 1);
        Assert.Equal(SimulationLod.Full, LodOf(sim, far));
    }

    [Fact]
    public void Lod_EmptyPrison_IsStatistical()
    {
        var world = content.BuildWorld();
        // No player at all: an empty server between visits.
        var match = MatchFactory.Create(world, content.Map, content.Items, content.Recipes,
            includeMapGuards: false, includePlayer: false);
        var sim = match.Simulation;
        var guard = SpawnGuard(sim, 10.5f, 7.5f, 0);

        RunTicks(sim, (int)LodSystem.EvaluationIntervalTicks * 2 + 1);
        Assert.Equal(SimulationLod.Statistical, LodOf(sim, guard));
    }

    // ---------- scheduling math (§7.10 rates) ----------

    [Fact]
    public void PerceptionIntervals_StretchWithLodAndLoad_ButNeverForFullDetail()
    {
        var budget = SimulationBudget.Balanced;

        // At rest: chase perceives 5× more often than patrol; LOD stretches the interval.
        Assert.Equal(2u, budget.PerceptionIntervalTicks(GuardAction.Chase, SimulationLod.Full));
        Assert.Equal(10u, budget.PerceptionIntervalTicks(GuardAction.Patrol, SimulationLod.Full));
        Assert.Equal(20u, budget.PerceptionIntervalTicks(GuardAction.Patrol, SimulationLod.Reduced));
        Assert.Equal(50u, budget.PerceptionIntervalTicks(GuardAction.Patrol, SimulationLod.Coarse));

        // Under load, degradation starves the low-priority end first (§7.10): Full is exempt.
        budget.DegradationScale = 4f;
        Assert.Equal(2u, budget.PerceptionIntervalTicks(GuardAction.Chase, SimulationLod.Full));
        Assert.Equal(10u, budget.PerceptionIntervalTicks(GuardAction.Patrol, SimulationLod.Full));
        Assert.Equal(80u, budget.PerceptionIntervalTicks(GuardAction.Patrol, SimulationLod.Reduced));
        Assert.Equal(200u, budget.PerceptionIntervalTicks(GuardAction.Patrol, SimulationLod.Coarse));
    }

    // ---------- discrete event resolution (§7.10 LOD 3/4) ----------

    [Fact]
    public void EventOnlyGuard_PatrolsByDiscreteHops_WithoutPathfindingOrNoise()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        sim.World.Get<Position>(match.Player) = new Position(5.5f, 7.5f, 0);

        // Guard on floor 1: nobody there to observe it → EventOnly.
        var guard = SpawnGuard(sim, 20.5f, 5.5f, 1);
        var route = sim.World.Get<PatrolRoute>(guard);
        var hop = match.Budget.EventOnlyHopTicks;

        RunTicks(sim, (int)(hop * 3));

        Assert.Equal(SimulationLod.EventOnly, LodOf(sim, guard));

        // It advanced along its patrol by teleport-hops: standing on a waypoint center,
        // with no pathfinding in flight and no path being walked.
        var position = sim.World.Get<Position>(guard);
        Assert.Contains(route.Waypoints, wp =>
            MathF.Abs(wp.X + 0.5f - position.X) < 0.01f && MathF.Abs(wp.Y + 0.5f - position.Y) < 0.01f);
        Assert.True(sim.World.Get<NavAgent>(guard).Idle, "EventOnly patrol must not use the pathfinding queue");

        // And the hops made no footstep noise (they are bookkeeping, not movement).
        Assert.Equal(0, match.Telemetry.SoundCounts.Values.Sum());
    }

    // ---------- dynamic budget (§12.3) ----------

    [Fact]
    public void AutoTuner_DegradesUnderLoad_AndRecovers()
    {
        var budget = SimulationBudget.Balanced;
        var tuner = new AiBudgetAutoTuner(budget, ticksPerSecond: 20);
        var tickInterval = 1.0 / 20;

        // Sustained overload: intervals stretch, pathfinding throttles — but never below 1.
        for (var i = 0; i < 10; i++)
            tuner.Adjust(tickInterval * 0.9);
        Assert.True(tuner.Degraded);
        Assert.True(budget.DegradationScale > 1f);
        Assert.True(budget.DegradationScale <= 8f, "degradation must be capped");
        Assert.Equal(1, budget.PathfindingBudgetPerTick);

        // Load gone: everything returns to the profile baseline, and no further.
        for (var i = 0; i < 30; i++)
            tuner.Adjust(tickInterval * 0.05);
        Assert.False(tuner.Degraded);
        Assert.Equal(1f, budget.DegradationScale);
        Assert.Equal(budget.BasePathfindingBudgetPerTick, budget.PathfindingBudgetPerTick);
    }

    // ---------- the Phase 9 deliverable: hundreds of NPCs, graceful behavior ----------

    [Fact]
    public void StressTest_HundredsOfGuards_StayScheduledAndBounded()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world, SimulationBudget.Balanced);
        var sim = match.Simulation;
        sim.World.Get<Position>(match.Player) = new Position(5.5f, 7.5f, 0);

        var rng = new Random(42);
        var guards = new List<Entity>();
        for (var i = 0; i < 300; i++)
        {
            var floor = i % 2;
            var x = rng.Next(2, world.Floor(floor).Width - 2);
            var y = rng.Next(2, world.Floor(floor).Height - 2);
            if (!world.IsWalkable(new TilePos(x, y, floor)))
            {
                x = 10 + i % 3;
                y = 7;
            }
            guards.Add(SpawnGuard(sim, x + 0.5f, y + 0.5f, floor));
        }

        RunTicks(sim, 200); // 10 simulated seconds

        // The shared pathfinding queue must not grow without bound: with 300 guards and a
        // budget of 4/tick, a run this long has to keep the backlog in check.
        Assert.True(match.Pathfinding.PendingCount < 300,
            $"pathfinding backlog exploded: {match.Pathfinding.PendingCount} pending");

        // Guards near the player run at full detail; the rest are degraded — the whole
        // point of §7.10 is that these two groups coexist.
        var nearFull = guards.Count(g =>
            sim.World.Get<Position>(g).Floor == 0
            && Distance(sim.World.Get<Position>(g), 5.5f, 7.5f) <= match.Budget.FullDetailRadiusTiles
            && LodOf(sim, g) == SimulationLod.Full);
        var degraded = guards.Count(g => LodOf(sim, g) >= SimulationLod.Coarse);
        Assert.True(nearFull > 0, "guards near the player must be at LOD Full");
        Assert.True(degraded > 50, $"far guards must be degraded (got {degraded})");

        // And the simulation still works end to end: every guard has a live patrol state.
        foreach (var guard in guards.Take(20))
            Assert.True(sim.World.Get<AiState>(guard).Action is GuardAction.Patrol
                or GuardAction.Investigate or GuardAction.Chase or GuardAction.Arrest);
    }

    private static float Distance(Position p, float x, float y)
    {
        var dx = p.X - x;
        var dy = p.Y - y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
