using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Arch.Core;
using Prison.Shared;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Items;
using Prison.Shared.Networking;
using Prison.Shared.Telemetry;
using Prison.Shared.World;

namespace Prison.Client;

/// <summary>An actor to draw, already reduced to what the renderer may know (observables only).</summary>
public readonly record struct ActorSprite(float X, float Y, int Floor, float FacingRadians, bool IsGuard);

/// <summary>
/// What <see cref="Main"/> renders and feeds input to, independent of where the match runs
/// (Pillar #5): <see cref="LocalMatchClient"/> embeds the shared simulation for single-player;
/// <see cref="RemoteMatchClient"/> mirrors an authoritative server through the replication
/// protocol. The renderer cannot tell the difference — by design.
/// </summary>
public interface IMatchClient : IDisposable
{
    string ModeLabel { get; }

    /// <summary>True once a world and a player exist (immediately in local; post-handshake online).</summary>
    bool Ready { get; }

    /// <summary>Set when the session is over (rejected handshake, lost connection).</summary>
    string? Error { get; }

    /// <summary>Only valid when <see cref="Ready"/>.</summary>
    WorldGrid World { get; }

    Position PlayerPosition { get; }

    float Threat { get; }

    /// <summary>Presence heat map debug overlay — local matches only (telemetry lives host-side).</summary>
    HeatMap? PresenceHeat { get; }

    /// <summary>Every actor except the local player.</summary>
    IEnumerable<ActorSprite> OtherActors { get; }

    /// <summary>True exactly once after the local player got arrested.</summary>
    bool ConsumeArrestSignal();

    void ApplyInput(float moveX, float moveY, bool running, bool useStairsJustPressed);

    void Update(double delta);
}

/// <summary>Single-player: embeds the Core Simulation directly (PLAN §4.1 local bootstrap).</summary>
public sealed class LocalMatchClient : IMatchClient
{
    private static readonly QueryDescription ActorQuery =
        new QueryDescription().WithAll<Position, Facing, Footsteps>();

    private readonly MatchHandle _match;
    private bool _arrested;

    public LocalMatchClient(string contentRoot)
    {
        var tiles = TileRegistry.LoadFromDirectory(Path.Combine(contentRoot, "tiles"));
        var items = ItemRegistry.LoadFromDirectory(Path.Combine(contentRoot, "items"));
        var recipes = RecipeDefinition.LoadFromDirectory(Path.Combine(contentRoot, "recipes"));
        var map = MapDefinition.Load(Path.Combine(contentRoot, "maps", "test_prison.json"));
        World = map.BuildWorld(tiles);

        _match = MatchFactory.Create(World, map, items, recipes);
        _match.Simulation.Events.Subscribe<ArrestEvent>(evt =>
        {
            if (evt.Prisoner == _match.Player)
                _arrested = true;
        });
    }

    public string ModeLabel => "solo";

    public bool Ready => true;

    public string? Error => null;

    public WorldGrid World { get; }

    public Position PlayerPosition => _match.Simulation.World.Get<Position>(_match.Player);

    public float Threat => _match.Simulation.World.Get<ThreatScore>(_match.Player).Threat;

    public HeatMap? PresenceHeat => _match.Escape.Presence;

    public IEnumerable<ActorSprite> OtherActors
    {
        get
        {
            var ecs = _match.Simulation.World;
            var actors = new List<ActorSprite>();
            ecs.Query(in ActorQuery, (Entity entity, ref Position pos, ref Facing facing) =>
            {
                if (entity != _match.Player)
                    actors.Add(new ActorSprite(pos.X, pos.Y, pos.Floor, facing.Radians, ecs.Has<GuardTag>(entity)));
            });
            return actors;
        }
    }

    public bool ConsumeArrestSignal()
    {
        var arrested = _arrested;
        _arrested = false;
        return arrested;
    }

    public void ApplyInput(float moveX, float moveY, bool running, bool useStairsJustPressed)
    {
        ref var input = ref _match.Simulation.World.Get<PlayerInput>(_match.Player);
        input.MoveX = moveX;
        input.MoveY = moveY;
        input.Running = running;
        input.UseStairs |= useStairsJustPressed; // sticky until a tick consumes it
    }

    public void Update(double delta) => _match.Simulation.Advance(delta);

    public void Dispose() => _match.Simulation.Dispose();
}

/// <summary>
/// Online: mirrors an authoritative server (PLAN §7.11). Sends intents, renders the
/// replicated view — zero gameplay logic on this side.
/// </summary>
public sealed class RemoteMatchClient : IMatchClient
{
    private readonly IClientTransport _transport;
    private readonly ClientSession _session;
    private (float MoveX, float MoveY, bool Running) _lastSentInput;
    private bool _inputSentOnce;

    public RemoteMatchClient(IClientTransport transport, string modeLabel, string playerName,
        Func<string, WorldGrid?> worldForMap)
    {
        ModeLabel = modeLabel;
        _transport = transport;
        _session = new ClientSession(_transport, playerName, worldForMap);
    }

    public RemoteMatchClient(string host, int port, string playerName, Func<string, WorldGrid?> worldForMap)
        : this(new TcpClientTransport(host, port), $"en ligne · {host}:{port}", playerName, worldForMap)
    {
    }

    public string ModeLabel { get; }

    /// <summary>The server-assigned identity of the local player (valid once Ready).</summary>
    public int NetId => _session.MyNetId;

    public bool Ready => _session.InGame && _session.World is not null && _session.Me is not null;

    public string? Error => _session.RejectReason
        ?? (_session.ConnectionAlive ? null : "connexion au serveur perdue");

    public WorldGrid World => _session.World!;

    public Position PlayerPosition
    {
        get
        {
            var me = _session.Me!.Value;
            return new Position(me.X, me.Y, me.Floor);
        }
    }

    public float Threat => _session.MyThreat;

    public HeatMap? PresenceHeat => null; // telemetry is host-side; nothing to overlay here

    public IEnumerable<ActorSprite> OtherActors =>
        _session.Actors.Values
            .Where(actor => actor.NetId != _session.MyNetId)
            .Select(actor => new ActorSprite(actor.X, actor.Y, actor.Floor,
                actor.FacingRadians, actor.IsGuard));

    // TODO(protocol v2): the server does not push an arrest event yet — add a ServerEvent
    // message rather than inferring it client-side from threat/position jumps.
    public bool ConsumeArrestSignal() => false;

    public void ApplyInput(float moveX, float moveY, bool running, bool useStairsJustPressed)
    {
        if (!Ready)
            return;
        // Intents only travel when they change (plus the one-shot stairs edge) — the server
        // keeps applying the last received input every tick.
        var input = (moveX, moveY, running);
        if (_inputSentOnce && input == _lastSentInput && !useStairsJustPressed)
            return;
        _session.SendInput(moveX, moveY, running, useStairsJustPressed);
        _lastSentInput = input;
        _inputSentOnce = true;
    }

    public void Update(double delta) => _session.Update();

    public void Dispose() => (_transport as IDisposable)?.Dispose();
}

/// <summary>
/// Player-hosted listen-server (PLAN §10.1 Community prison, in-client hosting): runs the
/// authoritative simulation + <see cref="ServerSession"/> in-process, while the hosting
/// player plays through the exact same protocol as everyone else — over a zero-copy
/// loopback transport — and friends join over TCP. No host-only code path exists in the
/// simulation (Pillar #3).
/// </summary>
public sealed class HostMatchClient : IMatchClient
{
    private readonly MatchHandle _match;
    private readonly ServerSession _server;
    private readonly RemoteMatchClient _local;
    private readonly CompositeServerTransport _transport;
    private bool _arrested;

    public HostMatchClient(string contentRoot, int port, string playerName)
    {
        var tiles = TileRegistry.LoadFromDirectory(Path.Combine(contentRoot, "tiles"));
        var items = ItemRegistry.LoadFromDirectory(Path.Combine(contentRoot, "items"));
        var recipes = RecipeDefinition.LoadFromDirectory(Path.Combine(contentRoot, "recipes"));
        var map = MapDefinition.Load(Path.Combine(contentRoot, "maps", "test_prison.json"));
        var world = map.BuildWorld(tiles);

        _match = MatchFactory.Create(world, map, items, recipes, includePlayer: false);
        var loopback = new LoopbackTransport();
        _transport = new CompositeServerTransport(loopback, new TcpServerTransport(port));
        _server = new ServerSession(_transport, _match, world, map);

        _local = new RemoteMatchClient(loopback.CreateClient(), $"hôte · port {port}", playerName,
            mapId => mapId == map.Id ? map.BuildWorld(tiles) : null);

        _match.Simulation.Events.Subscribe<ArrestEvent>(evt =>
        {
            if (evt.Prisoner.Id == _local.NetId)
                _arrested = true;
        });
    }

    public string ModeLabel => _local.ModeLabel;

    public bool Ready => _local.Ready;

    public string? Error => _local.Error;

    public WorldGrid World => _local.World;

    public Position PlayerPosition => _local.PlayerPosition;

    public float Threat => _local.Threat;

    public HeatMap? PresenceHeat => _match.Escape.Presence; // the host owns the telemetry

    public IEnumerable<ActorSprite> OtherActors => _local.OtherActors;

    public bool ConsumeArrestSignal()
    {
        var arrested = _arrested;
        _arrested = false;
        return arrested;
    }

    public void ApplyInput(float moveX, float moveY, bool running, bool useStairsJustPressed) =>
        _local.ApplyInput(moveX, moveY, running, useStairsJustPressed);

    public void Update(double delta)
    {
        _server.PumpNetwork();
        _match.Simulation.Advance(delta);
        _server.BroadcastState();
        _local.Update(delta);
    }

    public void Dispose()
    {
        _transport.Dispose();
        _match.Simulation.Dispose();
    }
}
