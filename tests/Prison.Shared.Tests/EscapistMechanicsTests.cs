using Arch.Core;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Interaction;
using Prison.Shared.Items;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>
/// Phase 3 (PLAN §7.8): escapist mechanics work purely through emergent interaction between
/// the mechanic, the shared world data, the event bus, and the Staff AI's physical senses.
/// </summary>
[Collection("content")]
public class EscapistMechanicsTests(TestContent content)
{
    // ---------- helpers ----------

    private MatchHandle NewMatch(WorldGrid world, bool includeMapGuards = false) =>
        MatchFactory.Create(world, content.Map, content.Items, content.Recipes,
            includeMapGuards: includeMapGuards);

    private static Entity SpawnGuard(Simulation sim, float x, float y, int floor, float facingRadians = 0f)
    {
        var guard = MatchFactory.SpawnGuard(sim, new MapDefinition.MapGuard
        {
            Floor = floor, X = (int)x, Y = (int)y, Patrol = [[(int)x, (int)y]],
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

    private static void Give(Simulation sim, Entity entity, params string[] itemIds)
    {
        var inventory = sim.World.Get<Inventory>(entity);
        inventory.Items.AddRange(itemIds);
    }

    private static void Request(Simulation sim, Entity entity, InteractionKind kind, TilePos target, string? id = null)
    {
        sim.World.Get<Interactor>(entity).Request = new InteractionRequest(kind, target, id);
    }

    private static void RunSeconds(Simulation sim, float seconds)
    {
        var ticks = (int)(seconds * sim.TicksPerSecond);
        for (var i = 0; i < ticks; i++)
            sim.Tick();
    }

    // ---------- content loading ----------

    [Fact]
    public void ItemAndRecipeContent_Loads()
    {
        Assert.True(content.Items.Count >= 9);
        Assert.Equal(5f, content.Items.Get("shovel").DigSeconds);
        Assert.Equal("guard", content.Items.Get("guard_uniform").DisguiseRole);
        Assert.Contains(content.Recipes, r => r.Id == "craft_shovel" && r.Output == "shovel");
    }

    // ---------- inventory & pickup ----------

    [Fact]
    public void MapItems_CanBePickedUp_WithinReachOnly()
    {
        var match = NewMatch(content.BuildWorld());
        var sim = match.Simulation;
        var spoonTile = new TilePos(6, 4, 0); // in the spawn cell, per the map data

        // Too far: standing across the prison, the request is ignored.
        Place(sim, match.Player, 20.5f, 12.5f, 0);
        Request(sim, match.Player, InteractionKind.PickUp, spoonTile);
        RunSeconds(sim, 0.2f);
        Assert.Empty(sim.World.Get<Inventory>(match.Player).Items);

        // Adjacent: picked up, entity gone, telemetry recorded it.
        Place(sim, match.Player, 5.5f, 4.5f, 0);
        Request(sim, match.Player, InteractionKind.PickUp, spoonTile);
        RunSeconds(sim, 0.2f);
        Assert.Contains("spoon", sim.World.Get<Inventory>(match.Player).Items);
        Request(sim, match.Player, InteractionKind.PickUp, spoonTile);
        RunSeconds(sim, 0.2f);
        Assert.Single(sim.World.Get<Inventory>(match.Player).Items); // it's gone from the world
        Assert.Contains(match.Telemetry.Entries, e => e.Type == nameof(ItemPickedUpEvent));
    }

    // ---------- crafting ----------

    [Fact]
    public void Crafting_ConsumesIngredients_AfterWorkTime()
    {
        var match = NewMatch(content.BuildWorld());
        var sim = match.Simulation;
        Place(sim, match.Player, 5.5f, 4.5f, 0);
        Give(sim, match.Player, "stick", "scrap_metal");

        var playerTile = new TilePos(5, 4, 0);
        Request(sim, match.Player, InteractionKind.Craft, playerTile, "craft_shovel");
        RunSeconds(sim, 1f);
        Assert.DoesNotContain("shovel", sim.World.Get<Inventory>(match.Player).Items); // still working

        RunSeconds(sim, 2.5f);
        var inventory = sim.World.Get<Inventory>(match.Player);
        Assert.Contains("shovel", inventory.Items);
        Assert.DoesNotContain("stick", inventory.Items);
        Assert.DoesNotContain("scrap_metal", inventory.Items);
        Assert.Contains(match.Telemetry.Entries, e => e.Type == nameof(ItemCraftedEvent));
    }

    [Fact]
    public void Crafting_WithoutIngredients_IsRejected()
    {
        var match = NewMatch(content.BuildWorld());
        var sim = match.Simulation;
        Place(sim, match.Player, 5.5f, 4.5f, 0);
        Give(sim, match.Player, "stick"); // missing scrap_metal

        Request(sim, match.Player, InteractionKind.Craft, new TilePos(5, 4, 0), "craft_shovel");
        RunSeconds(sim, 4f);
        Assert.DoesNotContain("shovel", sim.World.Get<Inventory>(match.Player).Items);
    }

    // ---------- digging ----------

    [Fact]
    public void Digging_TurnsDirtIntoTunnel_AndGuardsHearIt()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        Give(sim, match.Player, "shovel");

        // Player in the dirt strip; a guard ~8 tiles away, facing away (can only *hear*).
        Place(sim, match.Player, 5.5f, 2.5f, 0);
        var guard = SpawnGuard(sim, 13.5f, 2.5f, 0, facingRadians: 0f);

        var target = new TilePos(6, 2, 0);
        Request(sim, match.Player, InteractionKind.Dig, target);
        RunSeconds(sim, 6f); // shovel digs in 5s

        Assert.Equal("tunnel", world.Tiles.Get(world.Floor(0).GetFloorTile(6, 2)).Id);
        Assert.Contains(match.Telemetry.Entries, e => e.Type == nameof(TileDugEvent));
        Assert.True(match.Telemetry.SoundCounts.GetValueOrDefault(SoundKind.Digging) > 0);

        // The noise physically reached the guard and pulled it into an investigation.
        var state = sim.World.Get<AiState>(guard);
        var investigated = state.Action == GuardAction.Investigate
            || state.InvestigateTarget is not null
            || sim.World.Get<Beliefs>(guard).UnresolvedSound is not null
            || TilePos.EuclideanDistance(
                new TilePos((int)sim.World.Get<Position>(guard).X, (int)sim.World.Get<Position>(guard).Y, 0),
                target) < 7f;
        Assert.True(investigated, $"guard should react to digging noise, got {state.Action}");
    }

    [Fact]
    public void Digging_WithoutTool_OrOnConcreteFloor_IsRejected()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;

        // No tool.
        Place(sim, match.Player, 5.5f, 2.5f, 0);
        Request(sim, match.Player, InteractionKind.Dig, new TilePos(6, 2, 0));
        RunSeconds(sim, 6f);
        Assert.Equal("dirt", world.Tiles.Get(world.Floor(0).GetFloorTile(6, 2)).Id);

        // Tool, but concrete floor (can_dig = false).
        Give(sim, match.Player, "shovel");
        Place(sim, match.Player, 6.5f, 7.5f, 0);
        Request(sim, match.Player, InteractionKind.Dig, new TilePos(7, 7, 0));
        RunSeconds(sim, 6f);
        Assert.Equal("concrete_floor", world.Tiles.Get(world.Floor(0).GetFloorTile(7, 7)).Id);
    }

    [Fact]
    public void WalkingAwayFromWork_CancelsIt()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        Give(sim, match.Player, "shovel");
        Place(sim, match.Player, 5.5f, 2.5f, 0);

        Request(sim, match.Player, InteractionKind.Dig, new TilePos(6, 2, 0));
        RunSeconds(sim, 2f); // partway through

        ref var input = ref sim.World.Get<PlayerInput>(match.Player);
        input.MoveX = 1f;
        RunSeconds(sim, 1f);
        input.MoveX = 0f;
        RunSeconds(sim, 4f);

        Assert.Equal("dirt", world.Tiles.Get(world.Floor(0).GetFloorTile(6, 2)).Id);
        Assert.Null(sim.World.Get<Interactor>(match.Player).Work);
    }

    // ---------- fence cutting ----------

    [Fact]
    public void CuttingTheFence_OpensAnEscapeRoute()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        Give(sim, match.Player, "wire_cutters");

        var target = new TilePos(0, 1, 0); // perimeter fence, west side
        Assert.False(world.IsWalkable(target));

        Place(sim, match.Player, 1.5f, 1.5f, 0);
        Request(sim, match.Player, InteractionKind.CutFence, target);
        RunSeconds(sim, 5f); // wire cutters: 4s per the item data

        Assert.True(world.IsWalkable(target), "cut fence tile should now be passable");
        Assert.Contains(match.Telemetry.Entries, e => e.Type == nameof(FenceCutEvent));
        Assert.True(match.Telemetry.SoundCounts.GetValueOrDefault(SoundKind.MetalCutting) > 0);
    }

    // ---------- doors & lockpicking ----------

    [Fact]
    public void LockedDoor_Blocks_UntilPicked_ThenOpens()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        var doorTile = new TilePos(12, 17, 0); // locked workshop exterior door, per the map data

        Assert.False(world.IsWalkable(doorTile), "closed door must block movement");

        Place(sim, match.Player, 12.5f, 16.5f, 0);
        Give(sim, match.Player, "lockpick");

        // Toggling a locked door does nothing.
        Request(sim, match.Player, InteractionKind.ToggleDoor, doorTile);
        RunSeconds(sim, 0.2f);
        Assert.False(world.IsWalkable(doorTile));

        // Pick the lock (5s per the item data), then open it.
        Request(sim, match.Player, InteractionKind.Lockpick, doorTile);
        RunSeconds(sim, 5.5f);
        Assert.Contains(match.Telemetry.Entries, e => e.Type == nameof(DoorUnlockedEvent));

        Request(sim, match.Player, InteractionKind.ToggleDoor, doorTile);
        RunSeconds(sim, 0.2f);
        Assert.True(world.IsWalkable(doorTile), "unlocked+opened door must be passable");
        Assert.Contains(match.Telemetry.Entries, e => e.Type == nameof(DoorToggledEvent));
    }

    // ---------- disguises ----------

    [Fact]
    public void Disguise_DefeatsIdentificationAtRange_ButNotUpClose()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        Give(sim, match.Player, "guard_uniform");

        // Don the uniform, then stand 6 tiles in front of a guard in a lit corridor.
        Request(sim, match.Player, InteractionKind.SetDisguise, default, "guard_uniform");
        RunSeconds(sim, 0.2f);
        Assert.Equal("guard", sim.World.Get<Appearance>(match.Player).DisguiseRole);

        var guard = SpawnGuard(sim, 10.5f, 7.5f, 0, facingRadians: 0f);
        Place(sim, match.Player, 16.5f, 7.5f, 0);
        RunSeconds(sim, 2f);
        Assert.False(sim.World.Get<Beliefs>(guard).Suspects.ContainsKey(match.Player),
            "a disguised prisoner at range should read as staff");

        // Step within scrutiny range: the face gives it away, threat spikes.
        var threatBefore = sim.World.Get<ThreatScore>(match.Player).Threat;
        Place(sim, match.Player, 12.5f, 7.5f, 0);
        RunSeconds(sim, 1f);

        Assert.True(sim.World.Get<Beliefs>(guard).Suspects.TryGetValue(match.Player, out var belief));
        Assert.True(belief!.CurrentlyVisible);
        Assert.Contains(match.Telemetry.Entries, e => e.Type == nameof(DisguiseCompromisedEvent));
        Assert.True(sim.World.Get<ThreatScore>(match.Player).Threat > threatBefore + 30f,
            "being caught in disguise must spike observed threat");
    }

    // ---------- diversions ----------

    [Fact]
    public void ThrownRock_PullsGuardToTheNoise_NotToThePlayer()
    {
        var world = content.BuildWorld();
        var match = NewMatch(world);
        var sim = match.Simulation;
        Give(sim, match.Player, "rock");

        // Guard idles in the corridor facing away from everything relevant.
        var guard = SpawnGuard(sim, 10.5f, 7.5f, 0, facingRadians: -MathF.PI / 2f);
        Place(sim, match.Player, 6.5f, 12.5f, 0); // out of the guard's sight, common room

        var target = new TilePos(12, 11, 0); // impact ~4 tiles from the guard
        Request(sim, match.Player, InteractionKind.Throw, target, "rock");
        RunSeconds(sim, 1.5f);

        Assert.DoesNotContain("rock", sim.World.Get<Inventory>(match.Player).Items);
        var state = sim.World.Get<AiState>(guard);
        Assert.Equal(GuardAction.Investigate, state.Action);
        Assert.Equal(target, state.InvestigateTarget);
        Assert.Contains(match.Telemetry.Entries, e => e.Type == nameof(DiversionEvent));
    }

    // ---------- telemetry ----------

    [Fact]
    public void Telemetry_ProducesAnInspectableRecord()
    {
        var match = NewMatch(content.BuildWorld());
        var sim = match.Simulation;
        Place(sim, match.Player, 5.5f, 4.5f, 0);
        Request(sim, match.Player, InteractionKind.PickUp, new TilePos(6, 4, 0));
        RunSeconds(sim, 0.5f);

        Assert.NotEmpty(match.Telemetry.Entries);
        var json = match.Telemetry.ToJson();
        Assert.Contains(nameof(ItemPickedUpEvent), json);
    }
}
