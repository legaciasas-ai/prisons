using Arch.Core;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

[Collection("content")]
public class StaffAiTests(TestContent content)
{
    // ---------- helpers ----------

    private MatchHandle NewMatch(WorldGrid world, bool includeMapGuards = false) =>
        MatchFactory.Create(world, content.Map, includeMapGuards: includeMapGuards);

    private static Entity SpawnGuard(Simulation sim, float x, float y, int floor, float facingRadians = 0f,
        params (int x, int y)[] patrol)
    {
        var guard = MatchFactory.SpawnGuard(sim, new MapDefinition.MapGuard
        {
            Floor = floor,
            X = (int)x,
            Y = (int)y,
            Patrol = patrol.Length == 0 ? [[(int)x, (int)y]] : patrol.Select(p => new[] { p.x, p.y }).ToList(),
        });
        sim.World.Get<Position>(guard) = new Position(x, y, floor);
        sim.World.Get<Facing>(guard) = new Facing(facingRadians);
        return guard;
    }

    private static void Place(Simulation sim, Entity entity, float x, float y, int floor)
    {
        sim.World.Get<Position>(entity) = new Position(x, y, floor);
        if (sim.World.Has<Footsteps>(entity))
            sim.World.Set(entity, new Footsteps());
    }

    private static void RunSeconds(Simulation sim, float seconds)
    {
        var ticks = (int)(seconds * sim.TicksPerSecond);
        for (var i = 0; i < ticks; i++)
            sim.Tick();
    }

    // ---------- event bus ----------

    [Fact]
    public void EventBus_DeliversToSubscribersInOrder()
    {
        var bus = new EventBus();
        var received = new List<int>();
        bus.Subscribe<int>(v => received.Add(v));
        bus.Subscribe<int>(v => received.Add(v * 10));

        bus.Publish(7);

        Assert.Equal([7, 70], received);
        bus.Publish("unsubscribed type"); // no subscriber — must not throw
    }

    // ---------- perception ----------

    [Fact]
    public void Guard_SeesPrisonerInsideVisionCone_NotBehindItself()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        var guard = SpawnGuard(sim, 10.5f, 7.5f, floor: 0, facingRadians: 0f); // facing east

        Place(sim, match.Player, 15.5f, 7.5f, 0); // 5 tiles ahead, lit corridor
        RunSeconds(sim, 1f);

        var beliefs = sim.World.Get<Beliefs>(guard);
        Assert.True(beliefs.Suspects.TryGetValue(match.Player, out var belief));
        Assert.True(belief!.CurrentlyVisible);
        Assert.Equal(new TilePos(15, 7, 0), belief.LastKnown);
        Assert.Equal(1f, belief.Confidence);

        // Now behind the guard (still 5 tiles, but outside the 120° cone).
        var match2 = NewMatch(content.BuildWorld());
        var guard2 = SpawnGuard(match2.Simulation, 10.5f, 7.5f, 0, facingRadians: 0f);
        Place(match2.Simulation, match2.Player, 5.5f, 7.5f, 0);
        RunSeconds(match2.Simulation, 1f);

        Assert.False(match2.Simulation.World.Get<Beliefs>(guard2).Suspects.ContainsKey(match2.Player));
    }

    [Fact]
    public void Guard_CannotSeeThroughWalls()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        // Guard in the corridor looking north at a cell; wall segment at (12,6) blocks it.
        var guard = SpawnGuard(sim, 12.5f, 8.5f, 0, facingRadians: -MathF.PI / 2f);

        Place(sim, match.Player, 12.5f, 4.5f, 0); // inside the cell, straight line through the wall
        RunSeconds(sim, 1f);

        Assert.False(sim.World.Get<Beliefs>(guard).Suspects.ContainsKey(match.Player));
    }

    // ---------- memory ----------

    [Fact]
    public void Belief_ConfidenceDecaysAfterLosingSight_ThenIsForgotten()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        var guard = SpawnGuard(sim, 10.5f, 7.5f, 0, facingRadians: 0f);

        Place(sim, match.Player, 14.5f, 7.5f, 0);
        RunSeconds(sim, 1f); // seen: confidence 1

        Place(sim, match.Player, 5.5f, 15.5f, 0); // duck into the workshop, out of sight
        RunSeconds(sim, 5f);

        var beliefs = sim.World.Get<Beliefs>(guard);
        Assert.True(beliefs.Suspects.TryGetValue(match.Player, out var belief));
        Assert.False(belief!.CurrentlyVisible);
        Assert.InRange(belief.Confidence, 0.05f, 0.95f); // decaying, not yet forgotten
        Assert.Equal(new TilePos(14, 7, 0), belief.LastKnown); // last *seen*, not current, position

        RunSeconds(sim, 12f);
        Assert.False(sim.World.Get<Beliefs>(guard).Suspects.ContainsKey(match.Player)); // forgotten
    }

    // ---------- hearing ----------

    [Fact]
    public void RunningIsHeardThroughTheDoorway_TriggeringInvestigation_WalkingIsNot()
    {
        // Runner: sprints inside the common room; guard idles in the corridor ~5 tiles away.
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        var guard = SpawnGuard(sim, 6.5f, 7.5f, 0, facingRadians: MathF.PI); // facing west, away

        Place(sim, match.Player, 6.5f, 11.0f, 0);
        ref var input = ref sim.World.Get<PlayerInput>(match.Player);
        input.MoveY = 1f;
        input.Running = true;
        RunSeconds(sim, 1.5f);

        var state = sim.World.Get<AiState>(guard);
        Assert.Equal(GuardAction.Investigate, state.Action);
        Assert.NotNull(state.InvestigateTarget);
        Assert.True(TilePos.EuclideanDistance(state.InvestigateTarget!.Value, new TilePos(6, 12, 0)) <= 4f,
            $"investigation should head toward the noise, got {state.InvestigateTarget}");

        // Walker: same setup, no sprint — footsteps only carry 2 tiles, guard hears nothing.
        var match2 = NewMatch(content.BuildWorld());
        var sim2 = match2.Simulation;
        var guard2 = SpawnGuard(sim2, 6.5f, 7.5f, 0, facingRadians: MathF.PI);
        Place(sim2, match2.Player, 6.5f, 11.0f, 0);
        ref var input2 = ref sim2.World.Get<PlayerInput>(match2.Player);
        input2.MoveY = 1f;
        RunSeconds(sim2, 1.5f);

        Assert.Equal(GuardAction.Patrol, sim2.World.Get<AiState>(guard2).Action);
    }

    // ---------- suspicion ----------

    [Fact]
    public void BeingSeenInRestrictedZone_RaisesThreatUntilChase()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        // Guard in the corridor watching the stair room through the glass wall (south).
        var guard = SpawnGuard(sim, 24.5f, 8.5f, 0, facingRadians: MathF.PI / 2f);

        Place(sim, match.Player, 24.5f, 11.5f, 0); // inside the restricted stair room
        var initialThreat = sim.World.Get<ThreatScore>(match.Player).Threat;

        RunSeconds(sim, 3f);

        var threat = sim.World.Get<ThreatScore>(match.Player).Threat;
        Assert.True(threat > initialThreat + 30f, $"threat should climb while observed trespassing, got {threat}");
        Assert.True(threat >= ThreatScore.ChaseThreshold);

        var state = sim.World.Get<AiState>(guard);
        Assert.True(state.Action is GuardAction.Chase or GuardAction.Arrest,
            $"guard should pursue a trespasser, got {state.Action}");
    }

    // ---------- chase & arrest, end to end ----------

    [Fact]
    public void TrespasserIsChasedCaughtAndReturnedToCell()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        SpawnGuard(sim, 24.5f, 8.5f, 0, facingRadians: MathF.PI / 2f);

        Place(sim, match.Player, 24.5f, 11.5f, 0); // trespassing, player stands still

        var arrests = new List<ArrestEvent>();
        sim.Events.Subscribe<ArrestEvent>(arrests.Add);

        RunSeconds(sim, 20f);

        var arrest = Assert.Single(arrests);
        Assert.Equal(match.Player, arrest.Prisoner);

        var position = sim.World.Get<Position>(match.Player);
        var spawn = content.Map.PlayerSpawn.Position;
        Assert.Equal(spawn, new TilePos((int)position.X, (int)position.Y, position.Floor));
        Assert.True(sim.World.Get<ThreatScore>(match.Player).Threat <= 25f);
    }

    // ---------- radio ----------

    [Fact]
    public void Sighting_ReachesOtherGuardsOnlyAfterRadioDelay_WithReducedConfidence()
    {
        var world = content.BuildWorld();
        // Seal the stair room's door: the trespasser stays visible through the glass but is
        // physically unreachable, so the pursuit (and its radio traffic) outlives this test
        // instead of ending in a quick arrest.
        world.Floor(0).SetWallTile(22, 11, world.Tiles.IdOf("concrete_wall"));

        var match = NewMatch(world);
        var sim = match.Simulation;
        var spotter = SpawnGuard(sim, 24.5f, 8.5f, 0, facingRadians: MathF.PI / 2f);
        var faraway = SpawnGuard(sim, 6.5f, 15.5f, 0, facingRadians: MathF.PI); // workshop, no line of sight

        // The suspect's threatening history is already known (observed prior escapes, §7.7).
        sim.World.Get<ThreatScore>(match.Player) = new ThreatScore(80f);
        Place(sim, match.Player, 24.5f, 11.5f, 0); // restricted stair room, behind the glass

        ulong? alertTick = null;
        sim.Events.Subscribe<SuspectAlertEvent>(evt => alertTick ??= evt.Tick);

        RunSeconds(sim, 1.5f); // spotted through the glass; alert goes out; radio delay is 3s
        Assert.True(sim.World.Get<AiState>(spotter).Action is GuardAction.Chase or GuardAction.Arrest);
        Assert.NotNull(alertTick);
        Assert.False(sim.World.Get<Beliefs>(faraway).Suspects.ContainsKey(match.Player),
            "second-hand knowledge must not arrive before the radio delay");

        RunSeconds(sim, 3.5f);
        var relayed = sim.World.Get<Beliefs>(faraway).Suspects;
        Assert.True(relayed.TryGetValue(match.Player, out var belief), "radio broadcast should have arrived");
        Assert.False(belief!.CurrentlyVisible);
        Assert.True(belief.Confidence < 1f, "relayed knowledge is less certain than direct sight");
    }

    // ---------- patrol ----------

    [Fact]
    public void Guard_PatrolsItsRouteWhenNothingHappens()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        var guard = SpawnGuard(sim, 16.5f, 12.5f, 0, 0f, (6, 7), (20, 7));

        Place(sim, match.Player, 5.5f, 4.5f, 0); // player quietly in their cell

        var visited = new HashSet<TilePos>();
        for (var i = 0; i < 20 * 25; i++)
        {
            sim.Tick();
            var p = sim.World.Get<Position>(guard);
            visited.Add(new TilePos((int)p.X, (int)p.Y, p.Floor));
        }

        Assert.Equal(GuardAction.Patrol, sim.World.Get<AiState>(guard).Action);
        Assert.Contains(new TilePos(6, 7, 0), visited);
        Assert.Contains(new TilePos(20, 7, 0), visited);
    }
}
