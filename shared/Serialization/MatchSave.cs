using System.Text;
using Arch.Core;
using Microsoft.Extensions.Logging;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Interaction;
using Prison.Shared.Items;
using Prison.Shared.World;

namespace Prison.Shared.Serialization;

/// <summary>
/// The versioned binary save format (PLAN §7.12). Explicit field-by-field writes — never
/// serialized language objects — so refactors can't silently break saves. Tile ids are saved
/// as a *name table* + indices, so saves survive tile-registry reordering across updates.
/// A loader chain upgrades old versions to current (v1 saves predate door open/closed state).
/// </summary>
public static class MatchSave
{
    public const string Magic = "PRSN";

    /// <summary>v1: initial format. v2: adds the door 'open' flag.</summary>
    public const int CurrentVersion = 2;

    // Guards deliberately carry no ThreatScore (it is a prisoner-only component), so the
    // actor query must not require it — their threat slot is written as 0 in the stream.
    private static readonly QueryDescription Actors =
        new QueryDescription().WithAll<Position, Facing>();

    private static readonly QueryDescription Doors = new QueryDescription().WithAll<Door>();

    private static readonly QueryDescription Items = new QueryDescription().WithAll<WorldItem>();

    // ---------- save ----------

    public static void Save(MatchHandle match, WorldGrid world, string mapId, Stream stream,
        int version = CurrentVersion)
    {
        if (version is < 1 or > CurrentVersion)
            throw new ArgumentOutOfRangeException(nameof(version));

        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes(Magic));
        w.Write(version);
        w.Write(mapId);
        w.Write(match.Simulation.CurrentTick);

        // Tile name table: numeric layer ids resolve through names on load.
        w.Write(world.Tiles.Count);
        for (ushort id = 0; id < world.Tiles.Count; id++)
            w.Write(world.Tiles.Get(id).Id);

        // Layer data (includes every runtime mutation: tunnels, cut fences, door tiles).
        w.Write(world.FloorCount);
        for (var f = 0; f < world.FloorCount; f++)
        {
            var floor = world.Floor(f);
            w.Write(floor.Width);
            w.Write(floor.Height);
            w.Write(floor.AmbientLight);
            for (var y = 0; y < floor.Height; y++)
                for (var x = 0; x < floor.Width; x++)
                {
                    w.Write(floor.GetFloorTile(x, y));
                    w.Write(floor.GetWallTile(x, y));
                }
        }

        var ecs = match.Simulation.World;

        var doors = new List<Door>();
        ecs.Query(in Doors, (ref Door door) => doors.Add(door));
        w.Write(doors.Count);
        foreach (var door in doors)
        {
            WriteTilePos(w, door.Tile);
            w.Write(door.Locked);
            if (version >= 2)
                w.Write(door.Open);
        }

        var items = new List<WorldItem>();
        ecs.Query(in Items, (ref WorldItem item) => items.Add(item));
        w.Write(items.Count);
        foreach (var item in items)
        {
            w.Write(item.ItemId);
            WriteTilePos(w, item.Tile);
        }

        // Actors, with a stable save-index so beliefs can reference each other.
        var actors = new List<Entity>();
        ecs.Query(in Actors, (Entity entity, ref Position _, ref Facing _) => actors.Add(entity));
        var indexOf = actors.Select((entity, i) => (entity, i)).ToDictionary(p => p.entity, p => p.i);

        w.Write(actors.Count);
        foreach (var actor in actors)
        {
            var isGuard = ecs.Has<GuardTag>(actor);
            w.Write((byte)(isGuard ? 1 : 0));

            var position = ecs.Get<Position>(actor);
            w.Write(position.X);
            w.Write(position.Y);
            w.Write(position.Floor);
            w.Write(ecs.Get<Facing>(actor).Radians);
            w.Write(ecs.Has<ThreatScore>(actor) ? ecs.Get<ThreatScore>(actor).Threat : 0f);

            var inventory = ecs.Has<Inventory>(actor) ? ecs.Get<Inventory>(actor).Items : [];
            w.Write(inventory.Count);
            foreach (var itemId in inventory)
                w.Write(itemId);

            var role = ecs.Has<Appearance>(actor) ? ecs.Get<Appearance>(actor).DisguiseRole : null;
            w.Write(role is not null);
            if (role is not null)
                w.Write(role);

            if (!isGuard)
                continue;

            var vision = ecs.Get<VisionSense>(actor);
            w.Write(vision.MaxDistance);
            w.Write(vision.DarkDistance);
            w.Write(vision.FovDegrees);

            var route = ecs.Get<PatrolRoute>(actor);
            w.Write(route.Waypoints.Length);
            foreach (var wp in route.Waypoints)
                WriteTilePos(w, wp);
            w.Write(route.NextIndex);

            var state = ecs.Get<AiState>(actor);
            w.Write((byte)state.Action);
            w.Write(state.HasChaseTarget && indexOf.ContainsKey(state.ChaseTarget)
                ? indexOf[state.ChaseTarget]
                : -1);
            w.Write(state.InvestigateTarget is not null);
            if (state.InvestigateTarget is { } spot)
                WriteTilePos(w, spot);

            var beliefs = ecs.Get<Beliefs>(actor);
            var known = beliefs.Suspects.Where(kv => indexOf.ContainsKey(kv.Key)).ToList();
            w.Write(known.Count);
            foreach (var (suspect, belief) in known)
            {
                w.Write(indexOf[suspect]);
                WriteTilePos(w, belief.LastKnown);
                w.Write(belief.LastSeenTick);
                w.Write(belief.Confidence);
                w.Write(belief.CurrentlyVisible);
                w.Write(belief.SeenThroughDisguise);
            }
        }
    }

    // ---------- load ----------

    /// <summary>
    /// Restores a match into <paramref name="world"/>, which must be freshly built from the
    /// same map (<c>map.BuildWorld(tiles)</c>) — the caller keeps the reference for rendering,
    /// exactly as with <see cref="MatchFactory.Create"/>.
    /// </summary>
    public static MatchHandle Load(Stream stream, WorldGrid world, MapDefinition map,
        ItemRegistry itemRegistry, IReadOnlyList<RecipeDefinition> recipes,
        ILoggerFactory? loggerFactory = null)
    {
        var tiles = world.Tiles;
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != Magic)
            throw new InvalidDataException("Not a prison save file");
        var version = r.ReadInt32();
        if (version is < 1 or > CurrentVersion)
            throw new InvalidDataException($"Unsupported save version {version} (current {CurrentVersion})");

        var mapId = r.ReadString();
        if (mapId != map.Id)
            throw new InvalidDataException($"Save is for map '{mapId}', not '{map.Id}'");
        var tick = r.ReadUInt64();

        // Tile name table → current registry ids (renamed/removed tiles fail loudly).
        var tableSize = r.ReadInt32();
        var tileIdOf = new ushort[tableSize];
        for (var i = 0; i < tableSize; i++)
            tileIdOf[i] = tiles.IdOf(r.ReadString());

        // The caller rebuilt the world from the map (stairs/zones/lights); restore mutated layers.
        var floorCount = r.ReadInt32();
        if (floorCount != world.FloorCount)
            throw new InvalidDataException("Save floor count does not match the map");
        for (var f = 0; f < floorCount; f++)
        {
            var width = r.ReadInt32();
            var height = r.ReadInt32();
            _ = r.ReadSingle(); // ambient light: authoritative from the map
            var floor = world.Floor(f);
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    floor.SetFloorTile(x, y, tileIdOf[r.ReadUInt16()]);
                    floor.SetWallTile(x, y, tileIdOf[r.ReadUInt16()]);
                }
        }

        var bare = MatchFactory.CreateBare(world, map, itemRegistry, recipes, loggerFactory);
        var simulation = bare.Simulation;
        simulation.RestoreTick(tick);
        var ecs = simulation.World;

        var doorCount = r.ReadInt32();
        for (var i = 0; i < doorCount; i++)
        {
            var door = new Door { Tile = ReadTilePos(r), Locked = r.ReadBoolean() };
            door.Open = version >= 2 && r.ReadBoolean(); // v1 upgrade: doors load closed
            door.ApplyToWorld(world);
            ecs.Create(door);
        }

        var itemCount = r.ReadInt32();
        for (var i = 0; i < itemCount; i++)
        {
            var id = r.ReadString();
            ecs.Create(new WorldItem(id, ReadTilePos(r)));
        }

        // Actors: create first (so belief indices resolve), then wire beliefs.
        var actorCount = r.ReadInt32();
        var actors = new Entity[actorCount];
        var pendingBeliefs = new List<(int Guard, int Suspect, SuspectBelief Belief)>();
        var pendingChase = new List<(int Guard, int Target)>();
        Entity player = default;

        for (var i = 0; i < actorCount; i++)
        {
            var isGuard = r.ReadByte() == 1;
            var position = new Position(r.ReadSingle(), r.ReadSingle(), r.ReadInt32());
            var facing = new Facing(r.ReadSingle());
            var threat = new ThreatScore(r.ReadSingle());

            var inventory = new Inventory();
            var carried = r.ReadInt32();
            for (var c = 0; c < carried; c++)
                inventory.Items.Add(r.ReadString());

            var appearance = new Appearance(r.ReadBoolean() ? r.ReadString() : null);

            if (!isGuard)
            {
                actors[i] = ecs.Create(position, new PlayerInput(),
                    new MoveSpeed(MatchFactory.PlayerWalkSpeed), facing, new Footsteps(),
                    new PrisonerTag(), threat, inventory, new Interactor(), appearance);
                player = actors[i];
                continue;
            }

            var vision = new VisionSense(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var waypointCount = r.ReadInt32();
            var waypoints = new TilePos[waypointCount];
            for (var p = 0; p < waypointCount; p++)
                waypoints[p] = ReadTilePos(r);
            var route = new PatrolRoute { Waypoints = waypoints, NextIndex = r.ReadInt32() };

            var state = new AiState
            {
                Action = (GuardAction)r.ReadByte(),
                DecisionRequested = true, // re-decide immediately: nav state is not persisted
            };
            var chaseIndex = r.ReadInt32();
            if (r.ReadBoolean())
                state.InvestigateTarget = ReadTilePos(r);

            var beliefs = new Beliefs();
            var beliefCount = r.ReadInt32();
            for (var b = 0; b < beliefCount; b++)
            {
                var suspect = r.ReadInt32();
                pendingBeliefs.Add((i, suspect, new SuspectBelief
                {
                    LastKnown = ReadTilePos(r),
                    LastSeenTick = r.ReadUInt64(),
                    Confidence = r.ReadSingle(),
                    CurrentlyVisible = r.ReadBoolean(),
                    SeenThroughDisguise = r.ReadBoolean(),
                }));
            }

            // Same composition as MatchFactory.SpawnGuard — guards carry no ThreatScore.
            actors[i] = ecs.Create(position, new GuardTag(), facing, vision, new Footsteps(),
                state, beliefs, new NavAgent(), route);
            if (chaseIndex >= 0)
                pendingChase.Add((i, chaseIndex));
        }

        foreach (var (guard, suspect, belief) in pendingBeliefs)
            ecs.Get<Beliefs>(actors[guard]).Suspects[actors[suspect]] = belief;
        foreach (var (guard, target) in pendingChase)
        {
            var state = ecs.Get<AiState>(actors[guard]);
            state.ChaseTarget = actors[target];
            state.HasChaseTarget = true;
        }

        return new MatchHandle(simulation, bare.Pathfinding, player, bare.Telemetry, bare.Escape, bare.Replay);
    }

    private static void WriteTilePos(BinaryWriter w, TilePos pos)
    {
        w.Write(pos.X);
        w.Write(pos.Y);
        w.Write(pos.Floor);
    }

    private static TilePos ReadTilePos(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
}
