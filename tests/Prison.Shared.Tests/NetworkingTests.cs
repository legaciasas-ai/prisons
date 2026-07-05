using Arch.Core;
using Prison.Shared.AI;
using Prison.Shared.ECS.Components;
using Prison.Shared.Events;
using Prison.Shared.Interaction;
using Prison.Shared.Items;
using Prison.Shared.Networking;
using Prison.Shared.World;
using Xunit;

namespace Prison.Shared.Tests;

/// <summary>
/// Phase 8 (PLAN §7.11): versioned message protocol, authoritative server session, and the
/// client's replicated view — exercised over the deterministic in-memory transport, plus a
/// smoke test of the real TCP transport.
/// </summary>
[Collection("content")]
public class NetworkingTests(TestContent content)
{
    // ---------- helpers ----------

    private sealed record Host(ServerSession Server, WorldGrid World, LoopbackTransport Transport);

    private Host NewHost(bool includeMapGuards = false)
    {
        var world = content.BuildWorld();
        var match = MatchFactory.Create(world, content.Map, content.Items, content.Recipes,
            includeMapGuards: includeMapGuards, includePlayer: false);
        var transport = new LoopbackTransport();
        return new Host(new ServerSession(transport, match, world, content.Map), world, transport);
    }

    private ClientSession NewClient(Host host, string name = "tester") =>
        new(host.Transport.CreateClient(), name,
            mapId => mapId == content.Map.Id ? content.BuildWorld() : null);

    /// <summary>One full network frame: intents in, one simulation tick, state out.</summary>
    private static void PumpFrame(Host host, params ClientSession[] clients)
    {
        foreach (var client in clients)
            client.Update();
        host.Server.PumpNetwork();
        host.Server.Simulation.Tick();
        host.Server.BroadcastState();
        foreach (var client in clients)
            client.Update();
    }

    private static void PumpSeconds(Host host, float seconds, params ClientSession[] clients)
    {
        var ticks = (int)(seconds * host.Server.Simulation.TicksPerSecond);
        for (var i = 0; i < ticks; i++)
            PumpFrame(host, clients);
    }

    private ClientSession Join(Host host, string name = "tester")
    {
        var client = NewClient(host, name);
        PumpFrame(host, client);
        Assert.True(client.InGame, $"handshake should complete, got reject: {client.RejectReason}");
        return client;
    }

    private static Entity PrisonerOf(Host host, ClientSession client)
    {
        var (_, entity) = Assert.Single(host.Server.Players.Where(p => p.NetId == client.MyNetId));
        return entity;
    }

    // ---------- handshake ----------

    [Fact]
    public void Handshake_DeliversWorldAndIdentity()
    {
        var host = NewHost();
        var client = Join(host);

        Assert.True(client.MyNetId >= 0);
        Assert.Equal(host.Server.Simulation.TicksPerSecond, client.ServerTickRate);
        Assert.NotNull(client.World);
        Assert.Equal(1, host.Server.PlayerCount);

        PumpFrame(host, client);
        Assert.NotNull(client.Me);
        var me = client.Me!.Value;
        Assert.False(me.IsGuard);
        var spawn = content.Map.PlayerSpawn.Position;
        Assert.Equal(spawn.X + 0.5f, me.X, 3);
        Assert.Equal(spawn.Y + 0.5f, me.Y, 3);
    }

    [Fact]
    public void Handshake_RejectsProtocolVersionMismatch()
    {
        var host = NewHost();
        var raw = host.Transport.CreateClient();
        string? reject = null;
        var disconnected = false;
        raw.MessageReceived += message =>
        {
            if (Protocol.TypeOf(message) == MessageType.ServerReject)
            {
                using var r = Messages.OpenPayload(message);
                reject = Messages.ReadReject(r);
            }
        };
        raw.Disconnected += () => disconnected = true;

        raw.Poll();
        raw.Send(Messages.Hello("time traveller", Protocol.Version + 1));
        host.Server.PumpNetwork();
        raw.Poll();

        Assert.NotNull(reject);
        Assert.Contains("version", reject, StringComparison.OrdinalIgnoreCase);
        Assert.True(disconnected, "a rejected peer must be dropped");
        Assert.Equal(0, host.Server.PlayerCount);
    }

    [Fact]
    public void Welcome_CarriesPreJoinWorldMutations()
    {
        var host = NewHost();
        // The prison mutated before this player ever joined (someone dug a tunnel).
        host.World.Floor(0).SetFloorTile(6, 2, host.World.Tiles.IdOf("tunnel"));

        var client = Join(host);
        Assert.Equal("tunnel",
            client.World!.Tiles.Get(client.World.Floor(0).GetFloorTile(6, 2)).Id);
    }

    // ---------- server authority ----------

    [Fact]
    public void Input_MovesThePrisoner_OnTheServer()
    {
        var host = NewHost();
        var client = Join(host);
        PumpFrame(host, client);
        var before = client.Me!.Value;

        client.SendInput(0f, 1f); // walk south out of the open cell
        PumpSeconds(host, 1f, client);

        var after = client.Me!.Value;
        Assert.True(Math.Abs(after.Y - before.Y) > 0.5f,
            $"replicated position should follow server movement (Y {before.Y} → {after.Y})");

        // The authoritative position lives in the server's ECS, not in anything client-side.
        var serverPos = host.Server.Simulation.World.Get<Position>(PrisonerOf(host, client));
        Assert.Equal(serverPos.X, after.X, 3);
        Assert.Equal(serverPos.Y, after.Y, 3);
    }

    [Fact]
    public void TwoClients_SeeEachOtherMove()
    {
        var host = NewHost();
        var alice = Join(host, "alice");
        var bob = Join(host, "bob");
        PumpFrame(host, alice, bob);

        Assert.Equal(2, alice.Actors.Values.Count(a => !a.IsGuard));
        Assert.Equal(2, bob.Actors.Values.Count(a => !a.IsGuard));

        var bobSeenByAliceBefore = alice.Actors[bob.MyNetId];
        bob.SendInput(0f, 1f);
        PumpSeconds(host, 1f, alice, bob);

        var bobSeenByAliceAfter = alice.Actors[bob.MyNetId];
        Assert.True(Math.Abs(bobSeenByAliceAfter.Y - bobSeenByAliceBefore.Y) > 0.5f,
            "alice must see bob's movement through replication");
    }

    [Fact]
    public void MapGuards_AreReplicated()
    {
        var host = NewHost(includeMapGuards: true);
        var client = Join(host);
        PumpFrame(host, client);

        Assert.Equal(content.Map.Guards.Count, client.Actors.Values.Count(a => a.IsGuard));
    }

    // ---------- interactions & world replication ----------

    [Fact]
    public void Digging_ReplicatesTileChange_InventoryAndSound()
    {
        var host = NewHost();
        var client = Join(host);
        var prisoner = PrisonerOf(host, client);
        var sim = host.Server.Simulation;

        sim.World.Get<Inventory>(prisoner).Items.Add("shovel");
        sim.World.Get<Position>(prisoner) = new Position(5.5f, 2.5f, 0);
        sim.World.Set(prisoner, new Footsteps());

        client.SendInteraction(InteractionKind.Dig, new TilePos(6, 2, 0));
        PumpSeconds(host, 6f, client); // shovel digs in 5s

        // The client's mirrored world shows the tunnel — walkability/vision data included.
        Assert.Equal("tunnel",
            client.World!.Tiles.Get(client.World.Floor(0).GetFloorTile(6, 2)).Id);
        Assert.Contains("shovel", client.MyInventory);
        Assert.Contains(client.DrainSoundCues(), cue => cue.Kind == SoundKind.Digging);
    }

    [Fact]
    public void DoorToggle_ReplicatesToOtherClients()
    {
        var host = NewHost();
        var alice = Join(host, "alice");
        var bob = Join(host, "bob");

        // An unlocked door from the map data: find one and toggle it as alice.
        PumpFrame(host, alice, bob);
        var door = bob.Doors.First(d => !d.Locked);
        var prisoner = PrisonerOf(host, alice);
        host.Server.Simulation.World.Get<Position>(prisoner) =
            new Position(door.Tile.X + 0.5f, door.Tile.Y - 0.5f, door.Tile.Floor);
        host.Server.Simulation.World.Set(prisoner, new Footsteps());

        alice.SendInteraction(InteractionKind.ToggleDoor, door.Tile);
        PumpSeconds(host, 0.5f, alice, bob);

        var replicated = bob.Doors.First(d => d.Tile == door.Tile);
        Assert.True(replicated.Open, "bob must see the door alice opened");
        Assert.True(bob.World!.IsWalkable(door.Tile), "bob's mirrored world must be passable there");
    }

    // ---------- chat & disconnect ----------

    [Fact]
    public void Chat_IsRelayedWithSenderIdentity()
    {
        var host = NewHost();
        var alice = Join(host, "alice");
        var bob = Join(host, "bob");

        alice.SendChat("on creuse à la buanderie ce soir");
        PumpFrame(host, alice, bob);

        var (fromNetId, fromName, text) = Assert.Single(bob.ChatLog);
        Assert.Equal(alice.MyNetId, fromNetId);
        Assert.Equal("alice", fromName);
        Assert.Equal("on creuse à la buanderie ce soir", text);
    }

    [Fact]
    public void Disconnect_RemovesThePrisonerFromTheMatch()
    {
        var host = NewHost();
        var aliceTransport = host.Transport.CreateClient();
        var alice = new ClientSession(aliceTransport, "alice",
            mapId => mapId == content.Map.Id ? content.BuildWorld() : null);
        PumpFrame(host, alice);
        var bob = Join(host, "bob");
        PumpFrame(host, alice, bob);
        Assert.Equal(2, bob.Actors.Values.Count(a => !a.IsGuard));

        aliceTransport.Disconnect();
        PumpSeconds(host, 0.2f, bob);

        Assert.Equal(1, host.Server.PlayerCount);
        Assert.Equal(1, bob.Actors.Values.Count(a => !a.IsGuard));
    }

    // ---------- real TCP transport ----------

    [Fact]
    public async Task TcpTransport_CompletesHandshakeAndReplicates()
    {
        var world = content.BuildWorld();
        var match = MatchFactory.Create(world, content.Map, content.Items, content.Recipes,
            includeMapGuards: false, includePlayer: false);
        using var serverTransport = new TcpServerTransport(port: 0);
        var server = new ServerSession(serverTransport, match, world, content.Map);

        using var clientTransport = new TcpClientTransport("127.0.0.1", serverTransport.Port);
        var client = new ClientSession(clientTransport, "tcp-tester",
            mapId => mapId == content.Map.Id ? content.BuildWorld() : null);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (client.LastServerTick == 0 && DateTime.UtcNow < deadline)
        {
            client.Update();
            server.PumpNetwork();
            server.Simulation.Tick();
            server.BroadcastState();
            client.Update();
            await Task.Delay(5);
        }

        Assert.True(client.InGame, $"TCP handshake failed: {client.RejectReason}");
        Assert.True(client.LastServerTick > 0, "state broadcasts must arrive over TCP");
        Assert.NotNull(client.Me);
    }
}
