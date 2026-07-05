using Arch.Core;
using Microsoft.Extensions.Logging;
using Prison.Shared.AI;
using Prison.Shared.AI.Actions;
using Prison.Shared.AI.Memory;
using Prison.Shared.AI.Perception;
using Prison.Shared.AI.Reasoning;
using Prison.Shared.ECS.Components;
using Prison.Shared.ECS.Systems;
using Prison.Shared.Interaction;
using Prison.Shared.Items;
using Prison.Shared.Pathfinding;
using Prison.Shared.Telemetry;
using Prison.Shared.World;

namespace Prison.Shared;

/// <summary>A ready-to-run match: the simulation, its shared pathfinding queue, the local player, and the match records.</summary>
public sealed record MatchHandle(
    Simulation Simulation, PathfindingService Pathfinding, Entity Player,
    TelemetryRecorder Telemetry, EscapeRecorder Escape, ReplayRecorder Replay)
{
    /// <summary>Persists this match's full telemetry (journal, escape record, replay) to disk.</summary>
    public void WriteTelemetry(string directory) =>
        TelemetrySink.WriteSession(directory, Telemetry, Escape, Replay);
}

/// <summary>
/// Assembles a match the *same way everywhere* (Pillar #3): the Godot client, the headless
/// dedicated server, and tests all get an identical system lineup and identical entity
/// composition — only rendering and input sit outside.
/// </summary>
public static class MatchFactory
{
    public const float PlayerWalkSpeed = 3.5f;
    public const float InitialPrisonerThreat = 10f;

    /// <summary>The systems + recorders of a match, before any entity exists (used by save loading).</summary>
    public sealed record BareMatch(
        Simulation Simulation, PathfindingService Pathfinding,
        TelemetryRecorder Telemetry, EscapeRecorder Escape, ReplayRecorder Replay);

    /// <summary>Assembles the canonical system lineup with no entities spawned.</summary>
    public static BareMatch CreateBare(
        WorldGrid world, MapDefinition map,
        ItemRegistry items, IReadOnlyList<RecipeDefinition> recipes,
        ILoggerFactory? loggerFactory = null)
    {
        var simulation = new Simulation(logger: loggerFactory?.CreateLogger<Simulation>());
        var events = simulation.Events;
        var pathfinding = new PathfindingService(new HierarchicalPathfinder(world));
        var telemetry = new TelemetryRecorder(events);
        var replay = new ReplayRecorder(events);
        var escape = new EscapeRecorder();

        // Canonical system order: replay tick-stamp first, then player intent → interactions →
        // movement sounds → senses → beliefs → decisions → actions → navigation → shared
        // pathfinding budget, and the escape recorder sampling final positions last.
        simulation.AddSystem(replay);
        simulation.AddSystem(new PlayerMovementSystem(world));
        simulation.AddSystem(new StairTraversalSystem(world));
        simulation.AddSystem(new InteractionSystem(world, items, recipes, events));
        simulation.AddSystem(new FootstepSoundSystem(events));
        simulation.AddSystem(new PerceptionSystem(world, events));
        simulation.AddSystem(new HearingSystem(events));
        simulation.AddSystem(new Suspicion.SuspicionSystem(events));
        simulation.AddSystem(new RadioSystem(events));
        simulation.AddSystem(new MemoryDecaySystem());
        simulation.AddSystem(new AiDecisionSystem(events));
        simulation.AddSystem(new AiActionSystem(world, pathfinding, map.PlayerSpawn.Position, events));
        simulation.AddSystem(new NavAgentSystem());
        simulation.AddSystem(new PathfindingSystem(pathfinding));
        simulation.AddSystem(escape);

        return new BareMatch(simulation, pathfinding, telemetry, escape, replay);
    }

    /// <param name="includePlayer">
    /// False for a network host: peers spawn their own prisoners via <see cref="SpawnPrisoner"/>
    /// as they connect, and <see cref="MatchHandle.Player"/> stays default.
    /// </param>
    public static MatchHandle Create(
        WorldGrid world, MapDefinition map,
        ItemRegistry? items = null,
        IReadOnlyList<RecipeDefinition>? recipes = null,
        ILoggerFactory? loggerFactory = null,
        bool includeMapGuards = true,
        bool includePlayer = true)
    {
        items ??= new ItemRegistry();
        recipes ??= [];

        var bare = CreateBare(world, map, items, recipes, loggerFactory);
        var (simulation, pathfinding, telemetry, escape, replay) = bare;

        var player = includePlayer ? SpawnPrisoner(simulation, map) : default;

        if (includeMapGuards)
        {
            foreach (var guardSpawn in map.Guards)
                SpawnGuard(simulation, guardSpawn);
        }

        foreach (var mapItem in map.Items)
            simulation.World.Create(new WorldItem(mapItem.Id, mapItem.Position));

        foreach (var mapDoor in map.Doors)
        {
            var door = new Door { Tile = mapDoor.Position, Locked = mapDoor.Locked };
            door.ApplyToWorld(world); // doors start closed: written into the wall layer up front
            simulation.World.Create(door);
        }

        return new MatchHandle(simulation, pathfinding, player, telemetry, escape, replay);
    }

    /// <summary>Spawns a player-controllable prisoner at the map's spawn point — the same
    /// composition for the local single-player and for every connected network peer.</summary>
    public static Entity SpawnPrisoner(Simulation simulation, MapDefinition map)
    {
        var spawn = map.PlayerSpawn.Position;
        return simulation.World.Create(
            new Position(spawn.X + 0.5f, spawn.Y + 0.5f, spawn.Floor),
            new PlayerInput(),
            new MoveSpeed(PlayerWalkSpeed),
            new Facing(0f),
            new Footsteps(),
            new PrisonerTag(),
            new ThreatScore(InitialPrisonerThreat),
            new Inventory(),
            new Interactor(),
            new Appearance(null));
    }

    public static Entity SpawnGuard(Simulation simulation, MapDefinition.MapGuard guardSpawn)
    {
        var waypoints = guardSpawn.Patrol
            .Select(p => new TilePos(p[0], p[1], guardSpawn.Floor))
            .ToArray();
        if (waypoints.Length == 0)
            waypoints = [guardSpawn.Position];

        return simulation.World.Create(
            new Position(guardSpawn.X + 0.5f, guardSpawn.Y + 0.5f, guardSpawn.Floor),
            new GuardTag(),
            new Facing(0f),
            VisionSense.GuardDefault,
            new Footsteps(),
            new AiState(),
            new Beliefs(),
            new NavAgent(),
            new PatrolRoute { Waypoints = waypoints });
    }
}
