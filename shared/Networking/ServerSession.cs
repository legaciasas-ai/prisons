using Arch.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Interaction;
using Prison.Shared.Items;
using Prison.Shared.Serialization;
using Prison.Shared.World;

namespace Prison.Shared.Networking;

/// <summary>
/// The authoritative side of a networked match (PLAN §7.11): owns the simulation, decodes
/// client intents into ECS components (<see cref="PlayerInput"/>, <see cref="Interactor"/>),
/// and broadcasts observable state. Identical whether the host is official infrastructure
/// or a player's machine (Pillar #3) — only the transport differs.
/// </summary>
public sealed class ServerSession
{
    private static readonly QueryDescription ActorQuery =
        new QueryDescription().WithAll<Position, Facing, Footsteps>();

    private static readonly QueryDescription DoorQuery = new QueryDescription().WithAll<Door>();

    private static readonly QueryDescription ItemQuery = new QueryDescription().WithAll<WorldItem>();

    private readonly IServerTransport _transport;
    private readonly MatchHandle _match;
    private readonly WorldGrid _world;
    private readonly MapDefinition _map;
    private readonly ILogger _log;

    private readonly Dictionary<int, Peer> _peers = [];
    private readonly List<SoundCue> _pendingSounds = [];
    private readonly HashSet<TilePos> _pendingTileChanges = [];
    private ulong _lastBroadcastTick = ulong.MaxValue;

    private sealed class Peer
    {
        public required string Name { get; init; }

        public required Entity Prisoner { get; init; }
    }

    public ServerSession(IServerTransport transport, MatchHandle match, WorldGrid world,
        MapDefinition map, ILogger<ServerSession>? logger = null)
    {
        _transport = transport;
        _match = match;
        _world = world;
        _map = map;
        _log = logger ?? NullLogger<ServerSession>.Instance;

        _transport.MessageReceived += OnMessage;
        _transport.PeerDisconnected += OnPeerDisconnected;

        // Transient signals riding on the simulation's own event bus (§7.9): the network
        // layer is just one more subscriber, exactly like telemetry or the Staff AI.
        var events = match.Simulation.Events;
        events.Subscribe<SoundEmittedEvent>(evt =>
            _pendingSounds.Add(new SoundCue(evt.Position, evt.RadiusTiles, evt.Kind)));
        events.Subscribe<TileDugEvent>(evt => _pendingTileChanges.Add(evt.Position));
        events.Subscribe<FenceCutEvent>(evt => _pendingTileChanges.Add(evt.Position));
        events.Subscribe<DoorToggledEvent>(evt => _pendingTileChanges.Add(evt.Position));
    }

    public Simulation Simulation => _match.Simulation;

    public int PlayerCount => _peers.Count;

    /// <summary>Connected players as (net id, prisoner entity) pairs.</summary>
    public IEnumerable<(int NetId, Entity Prisoner)> Players =>
        _peers.Values.Select(p => (p.Prisoner.Id, p.Prisoner));

    /// <summary>Dispatches pending network events (connections, intents) on this thread.</summary>
    public void PumpNetwork() => _transport.Poll();

    /// <summary>
    /// Sends the observable state to every welcomed peer — once per simulation tick, no
    /// matter how often the host loop calls it.
    /// </summary>
    public void BroadcastState()
    {
        var tick = _match.Simulation.CurrentTick;
        if (tick == _lastBroadcastTick || _peers.Count == 0)
        {
            if (_peers.Count == 0)
            {
                _pendingSounds.Clear();
                _pendingTileChanges.Clear();
            }
            return;
        }
        _lastBroadcastTick = tick;

        var ecs = _match.Simulation.World;

        var actors = new List<ActorState>();
        ecs.Query(in ActorQuery, (Entity entity, ref Position pos, ref Facing facing, ref Footsteps steps) =>
        {
            var disguise = ecs.Has<Appearance>(entity) ? ecs.Get<Appearance>(entity).DisguiseRole : null;
            actors.Add(new ActorState(entity.Id, ecs.Has<GuardTag>(entity),
                pos.X, pos.Y, pos.Floor, facing.Radians, steps.ObservableSpeed, disguise));
        });

        var doors = new List<DoorState>();
        ecs.Query(in DoorQuery, (ref Door door) =>
            doors.Add(new DoorState(door.Tile, door.Locked, door.Open)));

        var items = new List<WorldItemState>();
        ecs.Query(in ItemQuery, (ref WorldItem item) =>
            items.Add(new WorldItemState(item.ItemId, item.Tile)));

        var changes = _pendingTileChanges.Select(tile =>
        {
            var floor = _world.Floor(tile.Floor);
            return new TileChange(tile,
                _world.Tiles.Get(floor.GetFloorTile(tile.X, tile.Y)).Id,
                _world.Tiles.Get(floor.GetWallTile(tile.X, tile.Y)).Id);
        }).ToList();
        var sounds = _pendingSounds.ToList();
        _pendingTileChanges.Clear();
        _pendingSounds.Clear();

        foreach (var (peerId, peer) in _peers)
        {
            var inventory = ecs.Get<Inventory>(peer.Prisoner).Items;
            var threat = ecs.Get<ThreatScore>(peer.Prisoner).Threat;
            _transport.Send(peerId, Messages.State(new StatePacket(
                tick, actors, doors, items, changes, sounds,
                peer.Prisoner.Id, threat, inventory)));
        }
    }

    private void OnMessage(int peerId, byte[] message)
    {
        using var r = Messages.OpenPayload(message);
        switch (Protocol.TypeOf(message))
        {
            case MessageType.ClientHello:
                OnHello(peerId, r);
                break;
            case MessageType.ClientInput when _peers.TryGetValue(peerId, out var peer):
                OnInput(peer, r);
                break;
            case MessageType.ClientInteraction when _peers.TryGetValue(peerId, out var peer):
                OnInteraction(peer, r);
                break;
            case MessageType.ClientChat when _peers.TryGetValue(peerId, out var peer):
                OnChat(peer, r);
                break;
            default:
                _log.LogWarning("Peer {Peer} sent {Type} before/without a valid handshake — ignored",
                    peerId, Protocol.TypeOf(message));
                break;
        }
    }

    private void OnHello(int peerId, BinaryReader r)
    {
        var (version, name) = Messages.ReadHello(r);
        if (version != Protocol.Version)
        {
            _log.LogWarning("Peer {Peer} rejected: protocol {Theirs} (server runs {Ours})",
                peerId, version, Protocol.Version);
            _transport.Send(peerId, Messages.Reject(
                $"Protocol version mismatch: server {Protocol.Version}, client {version}"));
            _transport.Disconnect(peerId);
            return;
        }

        if (_peers.ContainsKey(peerId))
            return; // duplicate hello

        var prisoner = MatchFactory.SpawnPrisoner(_match.Simulation, _map);
        _peers[peerId] = new Peer { Name = name, Prisoner = prisoner };
        _log.LogInformation("Player '{Name}' joined as net id {NetId} ({Count} online)",
            name, prisoner.Id, _peers.Count);

        _transport.Send(peerId, Messages.Welcome(prisoner.Id,
            _match.Simulation.TicksPerSecond, _map.Id,
            w => WorldSnapshot.Write(_world, w)));
    }

    private void OnInput(Peer peer, BinaryReader r)
    {
        var (moveX, moveY, running, useStairs) = Messages.ReadInput(r);
        ref var input = ref _match.Simulation.World.Get<PlayerInput>(peer.Prisoner);
        // Server-side validation: intent is clamped here; movement/collision rules run in
        // the simulation itself, so a hacked client still can't walk through walls.
        input.MoveX = Math.Clamp(moveX, -1f, 1f);
        input.MoveY = Math.Clamp(moveY, -1f, 1f);
        input.Running = running;
        input.UseStairs |= useStairs; // consumed by StairTraversalSystem on use
    }

    private void OnInteraction(Peer peer, BinaryReader r)
    {
        var (kind, target, id) = Messages.ReadInteraction(r);
        if (!Enum.IsDefined(kind))
            return;
        // Reach, tools, tile properties and timing are all validated by InteractionSystem.
        _match.Simulation.World.Get<Interactor>(peer.Prisoner).Request =
            new InteractionRequest(kind, target, id);
    }

    private void OnChat(Peer peer, BinaryReader r)
    {
        var text = Messages.ReadChat(r);
        if (string.IsNullOrWhiteSpace(text))
            return;
        var relay = Messages.ServerChat(peer.Prisoner.Id, peer.Name, text);
        foreach (var otherId in _peers.Keys)
            _transport.Send(otherId, relay);
    }

    private void OnPeerDisconnected(int peerId)
    {
        if (!_peers.Remove(peerId, out var peer))
            return;
        _match.Simulation.World.Destroy(peer.Prisoner);
        _log.LogInformation("Player '{Name}' disconnected ({Count} online)", peer.Name, _peers.Count);
    }
}
