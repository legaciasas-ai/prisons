using Prison.Shared.Interaction;
using Prison.Shared.Serialization;
using Prison.Shared.World;

namespace Prison.Shared.Networking;

/// <summary>
/// The client side of a networked match: sends *intents*, receives *observable state*
/// (PLAN §7.11). It runs no gameplay logic at all — the renderer/UI reads the replicated
/// views and the locally mirrored <see cref="World"/> tile layers (Pillar #5).
/// </summary>
public sealed class ClientSession
{
    private readonly IClientTransport _transport;
    private readonly string _playerName;
    private readonly Func<string, WorldGrid?> _worldForMap;
    private readonly Dictionary<int, ActorState> _actors = [];
    private readonly List<DoorState> _doors = [];
    private readonly List<WorldItemState> _items = [];
    private readonly List<SoundCue> _soundCues = [];
    private readonly List<(int FromNetId, string FromName, string Text)> _chatLog = [];

    public ClientSession(IClientTransport transport, string playerName,
        Func<string, WorldGrid?> worldForMap)
    {
        _transport = transport;
        _playerName = playerName;
        _worldForMap = worldForMap;
        _transport.Connected += () => _transport.Send(Messages.Hello(_playerName));
        _transport.Disconnected += () => ConnectionAlive = false;
        _transport.MessageReceived += OnMessage;
    }

    public bool ConnectionAlive { get; private set; } = true;

    /// <summary>True once the server accepted the handshake and the world arrived.</summary>
    public bool InGame { get; private set; }

    /// <summary>Set when the server refused the handshake (version mismatch, unknown map…).</summary>
    public string? RejectReason { get; private set; }

    public int MyNetId { get; private set; } = -1;

    public int ServerTickRate { get; private set; }

    public ulong LastServerTick { get; private set; }

    /// <summary>The local mirror of the map's tile layers, kept in sync by the server.</summary>
    public WorldGrid? World { get; private set; }

    public IReadOnlyDictionary<int, ActorState> Actors => _actors;

    public ActorState? Me => _actors.TryGetValue(MyNetId, out var me) ? me : null;

    public IReadOnlyList<DoorState> Doors => _doors;

    public IReadOnlyList<WorldItemState> Items => _items;

    public float MyThreat { get; private set; }

    public IReadOnlyList<string> MyInventory { get; private set; } = [];

    public IReadOnlyList<(int FromNetId, string FromName, string Text)> ChatLog => _chatLog;

    /// <summary>Dispatches queued network events; call once per frame.</summary>
    public void Update() => _transport.Poll();

    // ---- intents ----

    public void SendInput(float moveX, float moveY, bool running = false, bool useStairs = false) =>
        _transport.Send(Messages.Input(moveX, moveY, running, useStairs));

    public void SendInteraction(InteractionKind kind, TilePos target, string? id = null) =>
        _transport.Send(Messages.Interaction(kind, target, id));

    public void SendChat(string text) => _transport.Send(Messages.Chat(text));

    /// <summary>Drains the sound cues accumulated since the last call (for audio/UI).</summary>
    public List<SoundCue> DrainSoundCues()
    {
        var cues = new List<SoundCue>(_soundCues);
        _soundCues.Clear();
        return cues;
    }

    // ---- message handling ----

    private void OnMessage(byte[] message)
    {
        using var r = Messages.OpenPayload(message);
        switch (Protocol.TypeOf(message))
        {
            case MessageType.ServerWelcome:
            {
                var (netId, tickRate, mapId) = Messages.ReadWelcomeHeader(r);
                var world = _worldForMap(mapId);
                if (world is null)
                {
                    RejectReason = $"Unknown map '{mapId}' — client content out of date?";
                    ConnectionAlive = false;
                    _transport.Disconnect();
                    return;
                }
                WorldSnapshot.Apply(world, r);
                World = world;
                MyNetId = netId;
                ServerTickRate = tickRate;
                InGame = true;
                break;
            }
            case MessageType.ServerReject:
                RejectReason = Messages.ReadReject(r);
                ConnectionAlive = false;
                break;
            case MessageType.ServerState:
                ApplyState(Messages.ReadState(r));
                break;
            case MessageType.ServerChat:
                _chatLog.Add(Messages.ReadServerChat(r));
                break;
        }
    }

    private void ApplyState(StatePacket state)
    {
        // Broadcasts are per-tick; ignore anything stale or arriving before the welcome.
        if (!InGame || (LastServerTick != 0 && state.Tick <= LastServerTick))
            return;
        LastServerTick = state.Tick;

        _actors.Clear();
        foreach (var actor in state.Actors)
            _actors[actor.NetId] = actor;

        _doors.Clear();
        _doors.AddRange(state.Doors);

        _items.Clear();
        _items.AddRange(state.Items);

        _soundCues.AddRange(state.Sounds);

        if (World is { } world)
        {
            foreach (var change in state.TileChanges)
            {
                var floor = world.Floor(change.Tile.Floor);
                floor.SetFloorTile(change.Tile.X, change.Tile.Y, world.Tiles.IdOf(change.FloorTileId));
                floor.SetWallTile(change.Tile.X, change.Tile.Y, world.Tiles.IdOf(change.WallTileId));
            }
        }

        MyNetId = state.YourNetId;
        MyThreat = state.YourThreat;
        MyInventory = state.YourInventory;
    }
}
