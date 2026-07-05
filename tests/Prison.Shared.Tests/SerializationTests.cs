using Arch.Core;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Interaction;
using Prison.Shared.Items;
using Prison.Shared.Serialization;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>
/// Phase 7 (PLAN §7.12): the versioned binary save format. A running match saves and reloads
/// exactly (world mutations, doors, items, actors, guard beliefs, tick), and an intentionally
/// old-format (v1) save still loads through the upgrade chain.
/// </summary>
[Collection("content")]
public class SerializationTests(TestContent content)
{
    private static readonly QueryDescription GuardQuery = new QueryDescription().WithAll<GuardTag>();
    private static readonly QueryDescription DoorQuery = new QueryDescription().WithAll<Door>();
    private static readonly QueryDescription ItemQuery = new QueryDescription().WithAll<WorldItem>();

    // ---------- helpers ----------

    private MatchHandle NewMatch(WorldGrid world) =>
        MatchFactory.Create(world, content.Map, content.Items, content.Recipes,
            includeMapGuards: false);

    private MemoryStream Save(MatchHandle match, WorldGrid world, int version = MatchSave.CurrentVersion)
    {
        var stream = new MemoryStream();
        MatchSave.Save(match, world, content.Map.Id, stream, version);
        stream.Position = 0;
        return stream;
    }

    private MatchHandle Load(Stream stream, WorldGrid freshWorld) =>
        MatchSave.Load(stream, freshWorld, content.Map, content.Items, content.Recipes);

    private static Entity SpawnGuard(Simulation sim, float x, float y, int floor, float facingRadians = 0f)
    {
        var guard = MatchFactory.SpawnGuard(sim, new MapDefinition.MapGuard
        {
            Floor = floor, X = (int)x, Y = (int)y, Patrol = [[(int)x, (int)y], [(int)x + 2, (int)y]],
        });
        sim.World.Get<Position>(guard) = new Position(x, y, floor);
        sim.World.Get<Facing>(guard) = new Facing(facingRadians);
        return guard;
    }

    private static void RunSeconds(Simulation sim, float seconds)
    {
        var ticks = (int)(seconds * sim.TicksPerSecond);
        for (var i = 0; i < ticks; i++)
            sim.Tick();
    }

    private static Entity SingleGuard(Simulation sim)
    {
        var guards = new List<Entity>();
        sim.World.Query(in GuardQuery, (Entity entity, ref GuardTag _) => guards.Add(entity));
        return Assert.Single(guards);
    }

    private static Door DoorAt(Simulation sim, TilePos tile)
    {
        Door? found = null;
        sim.World.Query(in DoorQuery, (ref Door door) =>
        {
            if (door.Tile == tile)
                found = door;
        });
        Assert.NotNull(found);
        return found!;
    }

    private static List<WorldItem> WorldItems(Simulation sim)
    {
        var items = new List<WorldItem>();
        sim.World.Query(in ItemQuery, (ref WorldItem item) => items.Add(item));
        return items;
    }

    /// <summary>A played-in match: dug tunnel, unlocked+opened door, disguise, a guard with a belief.</summary>
    private (MatchHandle Match, WorldGrid World, Entity Guard, TilePos DoorTile) BuildPlayedMatch()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;

        // Dig a tunnel through the dirt strip (a runtime tile-layer mutation).
        sim.World.Get<Inventory>(match.Player).Items.AddRange(["shovel", "guard_uniform", "rock"]);
        sim.World.Get<Position>(match.Player) = new Position(5.5f, 2.5f, 0);
        sim.World.Get<Interactor>(match.Player).Request =
            new InteractionRequest(InteractionKind.Dig, new TilePos(6, 2, 0));
        RunSeconds(sim, 6f);

        // Unlock and open the workshop door (runtime door state).
        var doorTile = new TilePos(12, 17, 0);
        var door = DoorAt(sim, doorTile);
        door.Locked = false;
        door.Open = true;
        door.ApplyToWorld(world);

        // Don a disguise, then let a guard see through it up close: belief + threat spike.
        sim.World.Get<Interactor>(match.Player).Request =
            new InteractionRequest(InteractionKind.SetDisguise, default, "guard_uniform");
        RunSeconds(sim, 0.2f);
        var guard = SpawnGuard(sim, 10.5f, 7.5f, 0);
        sim.World.Get<Position>(match.Player) = new Position(12.5f, 7.5f, 0);
        sim.World.Set(match.Player, new Footsteps());
        RunSeconds(sim, 1f);

        Assert.True(sim.World.Get<Beliefs>(guard).Suspects.ContainsKey(match.Player),
            "setup: the guard must have formed a belief about the player");
        return (match, world, guard, doorTile);
    }

    // ---------- round trip ----------

    [Fact]
    public void SaveLoad_RoundTripsTheFullMatchState()
    {
        var (match, world, guard, doorTile) = BuildPlayedMatch();
        var sim = match.Simulation;

        var loadedWorld = content.BuildWorld();
        var loaded = Load(Save(match, world), loadedWorld);
        var loadedSim = loaded.Simulation;

        // Tick restored.
        Assert.Equal(sim.CurrentTick, loadedSim.CurrentTick);

        // World-layer mutations restored (the dug tunnel).
        Assert.Equal("tunnel", loadedWorld.Tiles.Get(loadedWorld.Floor(0).GetFloorTile(6, 2)).Id);

        // Door state restored, including its wall-layer projection.
        var loadedDoor = DoorAt(loadedSim, doorTile);
        Assert.False(loadedDoor.Locked);
        Assert.True(loadedDoor.Open);
        Assert.True(loadedWorld.IsWalkable(doorTile), "an open door must stay passable after load");

        // World items restored.
        Assert.Equal(
            WorldItems(sim).OrderBy(i => i.ItemId).ToArray(),
            WorldItems(loadedSim).OrderBy(i => i.ItemId).ToArray());

        // Player restored: position, threat, inventory, disguise.
        var playerPos = sim.World.Get<Position>(match.Player);
        var loadedPos = loadedSim.World.Get<Position>(loaded.Player);
        Assert.Equal(playerPos, loadedPos);
        Assert.Equal(sim.World.Get<ThreatScore>(match.Player).Threat,
            loadedSim.World.Get<ThreatScore>(loaded.Player).Threat);
        Assert.Equal(sim.World.Get<Inventory>(match.Player).Items,
            loadedSim.World.Get<Inventory>(loaded.Player).Items);
        Assert.Equal("guard", loadedSim.World.Get<Appearance>(loaded.Player).DisguiseRole);

        // Guard restored: position, vision, patrol route, and its belief about the player.
        var loadedGuard = SingleGuard(loadedSim);
        Assert.Equal(sim.World.Get<Position>(guard), loadedSim.World.Get<Position>(loadedGuard));
        Assert.Equal(sim.World.Get<VisionSense>(guard), loadedSim.World.Get<VisionSense>(loadedGuard));

        var route = sim.World.Get<PatrolRoute>(guard);
        var loadedRoute = loadedSim.World.Get<PatrolRoute>(loadedGuard);
        Assert.Equal(route.Waypoints, loadedRoute.Waypoints);
        Assert.Equal(route.NextIndex, loadedRoute.NextIndex);

        var belief = sim.World.Get<Beliefs>(guard).Suspects[match.Player];
        Assert.True(loadedSim.World.Get<Beliefs>(loadedGuard).Suspects
            .TryGetValue(loaded.Player, out var loadedBelief));
        Assert.Equal(belief.LastKnown, loadedBelief!.LastKnown);
        Assert.Equal(belief.LastSeenTick, loadedBelief.LastSeenTick);
        Assert.Equal(belief.Confidence, loadedBelief.Confidence);
        Assert.Equal(belief.SeenThroughDisguise, loadedBelief.SeenThroughDisguise);
    }

    [Fact]
    public void LoadedMatch_KeepsSimulating()
    {
        var (match, world, _, _) = BuildPlayedMatch();

        var loadedWorld = content.BuildWorld();
        var loaded = Load(Save(match, world), loadedWorld);

        // The loaded guard saw the player up close pre-save (threat spiked past the chase
        // threshold), so the restored simulation should keep acting on that state.
        RunSeconds(loaded.Simulation, 2f);
        var guard = SingleGuard(loaded.Simulation);
        var action = loaded.Simulation.World.Get<AiState>(guard).Action;
        Assert.True(action is GuardAction.Chase or GuardAction.Arrest or GuardAction.Investigate,
            $"the loaded guard should still be reacting to the player, got {action}");
    }

    [Fact]
    public void ChaseTarget_SurvivesTheRoundTrip()
    {
        var (match, world, guard, _) = BuildPlayedMatch();
        var sim = match.Simulation;

        // Push the guard into an explicit chase before saving.
        RunSeconds(sim, 2f);
        var state = sim.World.Get<AiState>(guard);
        if (!state.HasChaseTarget)
        {
            state.Action = GuardAction.Chase;
            state.ChaseTarget = match.Player;
            state.HasChaseTarget = true;
        }

        var loaded = Load(Save(match, world), content.BuildWorld());
        var loadedState = loaded.Simulation.World.Get<AiState>(SingleGuard(loaded.Simulation));
        Assert.True(loadedState.HasChaseTarget);
        Assert.Equal(loaded.Player, loadedState.ChaseTarget);
    }

    // ---------- version migration ----------

    [Fact]
    public void V1Save_LoadsThroughTheUpgradeChain_DoorsDefaultClosed()
    {
        var (match, world, _, doorTile) = BuildPlayedMatch();

        var loadedWorld = content.BuildWorld();
        var loaded = Load(Save(match, world, version: 1), loadedWorld);

        // v1 predates the door 'open' flag: the unlock state survives, the door loads closed.
        var loadedDoor = DoorAt(loaded.Simulation, doorTile);
        Assert.False(loadedDoor.Locked);
        Assert.False(loadedDoor.Open);
        Assert.False(loadedWorld.IsWalkable(doorTile), "a v1-loaded door must be closed");

        // Everything else still came through.
        Assert.Equal("tunnel", loadedWorld.Tiles.Get(loadedWorld.Floor(0).GetFloorTile(6, 2)).Id);
        Assert.Equal(match.Simulation.CurrentTick, loaded.Simulation.CurrentTick);
    }

    // ---------- rejection ----------

    [Fact]
    public void Load_RejectsGarbageData()
    {
        var stream = new MemoryStream("NOPE this is not a save file"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => Load(stream, content.BuildWorld()));
    }

    [Fact]
    public void Load_RejectsUnsupportedFutureVersion()
    {
        var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write("PRSN"u8.ToArray());
            w.Write(MatchSave.CurrentVersion + 1);
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => Load(stream, content.BuildWorld()));
    }

    [Fact]
    public void Load_RejectsASaveForAnotherMap()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var stream = new MemoryStream();
        MatchSave.Save(match, world, "some_other_map", stream);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => Load(stream, content.BuildWorld()));
    }
}
