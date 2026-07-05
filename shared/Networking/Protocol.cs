using System.Text;
using Prison.Shared.Events;
using Prison.Shared.Interaction;
using Prison.Shared.World;

namespace Prison.Shared.Networking;

/// <summary>
/// The explicit, versioned message protocol (PLAN §7.11): deterministic message types over
/// any transport — never engine RPC magic. Every message is one discrete frame:
/// <c>[type: byte][payload]</c>. Bump <see cref="Version"/> on any wire-format change; the
/// handshake rejects mismatched peers up front instead of desyncing later.
/// </summary>
public static class Protocol
{
    public const int Version = 1;

    public static MessageType TypeOf(byte[] message) => (MessageType)message[0];
}

public enum MessageType : byte
{
    ClientHello = 1,
    ClientInput = 2,
    ClientInteraction = 3,
    ClientChat = 4,

    ServerWelcome = 10,
    ServerReject = 11,
    ServerState = 12,
    ServerChat = 13,
}

// ---- replicated views (server → client), pure observable data ----

/// <summary>What a client may know about an actor — position, facing, observable speed and
/// worn disguise. Never beliefs, threat internals, or anything a player couldn't see.</summary>
public readonly record struct ActorState(
    int NetId, bool IsGuard, float X, float Y, int Floor,
    float FacingRadians, float ObservableSpeed, string? DisguiseRole);

public readonly record struct DoorState(TilePos Tile, bool Locked, bool Open);

public readonly record struct WorldItemState(string ItemId, TilePos Tile);

/// <summary>A runtime tile mutation (tunnel dug, fence cut, door toggled), by tile name.</summary>
public readonly record struct TileChange(TilePos Tile, string FloorTileId, string WallTileId);

/// <summary>A sound the client should render (audio cue / UI ping) — same physical event the AI heard.</summary>
public readonly record struct SoundCue(TilePos Position, float RadiusTiles, SoundKind Kind);

/// <summary>One server state broadcast: the shared world view plus the recipient's private data.</summary>
public sealed record StatePacket(
    ulong Tick,
    IReadOnlyList<ActorState> Actors,
    IReadOnlyList<DoorState> Doors,
    IReadOnlyList<WorldItemState> Items,
    IReadOnlyList<TileChange> TileChanges,
    IReadOnlyList<SoundCue> Sounds,
    int YourNetId,
    float YourThreat,
    IReadOnlyList<string> YourInventory);

/// <summary>Binary encoding of every protocol message. One writer/reader pair per message type.</summary>
public static class Messages
{
    // ---- client → server ----

    public static byte[] Hello(string playerName, int protocolVersion = Protocol.Version) =>
        Encode(MessageType.ClientHello, w =>
        {
            w.Write(protocolVersion);
            w.Write(playerName);
        });

    public static (int ProtocolVersion, string PlayerName) ReadHello(BinaryReader r) =>
        (r.ReadInt32(), r.ReadString());

    public static byte[] Input(float moveX, float moveY, bool running, bool useStairs) =>
        Encode(MessageType.ClientInput, w =>
        {
            w.Write(moveX);
            w.Write(moveY);
            w.Write(running);
            w.Write(useStairs);
        });

    public static (float MoveX, float MoveY, bool Running, bool UseStairs) ReadInput(BinaryReader r) =>
        (r.ReadSingle(), r.ReadSingle(), r.ReadBoolean(), r.ReadBoolean());

    public static byte[] Interaction(InteractionKind kind, TilePos target, string? id) =>
        Encode(MessageType.ClientInteraction, w =>
        {
            w.Write((byte)kind);
            Write(w, target);
            WriteNullable(w, id);
        });

    public static (InteractionKind Kind, TilePos Target, string? Id) ReadInteraction(BinaryReader r) =>
        ((InteractionKind)r.ReadByte(), ReadTilePos(r), ReadNullable(r));

    public static byte[] Chat(string text) =>
        Encode(MessageType.ClientChat, w => w.Write(text));

    public static string ReadChat(BinaryReader r) => r.ReadString();

    // ---- server → client ----

    /// <summary>Welcome carries the full tile-layer snapshot so a late joiner sees every
    /// pre-join mutation (dug tunnels, cut fences) without replaying history.</summary>
    public static byte[] Welcome(int yourNetId, int tickRate, string mapId,
        Action<BinaryWriter> writeWorldSnapshot) =>
        Encode(MessageType.ServerWelcome, w =>
        {
            w.Write(yourNetId);
            w.Write(tickRate);
            w.Write(mapId);
            writeWorldSnapshot(w);
        });

    public static (int YourNetId, int TickRate, string MapId) ReadWelcomeHeader(BinaryReader r) =>
        (r.ReadInt32(), r.ReadInt32(), r.ReadString());

    public static byte[] Reject(string reason) =>
        Encode(MessageType.ServerReject, w => w.Write(reason));

    public static string ReadReject(BinaryReader r) => r.ReadString();

    public static byte[] ServerChat(int fromNetId, string fromName, string text) =>
        Encode(MessageType.ServerChat, w =>
        {
            w.Write(fromNetId);
            w.Write(fromName);
            w.Write(text);
        });

    public static (int FromNetId, string FromName, string Text) ReadServerChat(BinaryReader r) =>
        (r.ReadInt32(), r.ReadString(), r.ReadString());

    public static byte[] State(StatePacket state) =>
        Encode(MessageType.ServerState, w =>
        {
            w.Write(state.Tick);

            w.Write(state.Actors.Count);
            foreach (var actor in state.Actors)
            {
                w.Write(actor.NetId);
                w.Write(actor.IsGuard);
                w.Write(actor.X);
                w.Write(actor.Y);
                w.Write(actor.Floor);
                w.Write(actor.FacingRadians);
                w.Write(actor.ObservableSpeed);
                WriteNullable(w, actor.DisguiseRole);
            }

            w.Write(state.Doors.Count);
            foreach (var door in state.Doors)
            {
                Write(w, door.Tile);
                w.Write(door.Locked);
                w.Write(door.Open);
            }

            w.Write(state.Items.Count);
            foreach (var item in state.Items)
            {
                w.Write(item.ItemId);
                Write(w, item.Tile);
            }

            w.Write(state.TileChanges.Count);
            foreach (var change in state.TileChanges)
            {
                Write(w, change.Tile);
                w.Write(change.FloorTileId);
                w.Write(change.WallTileId);
            }

            w.Write(state.Sounds.Count);
            foreach (var sound in state.Sounds)
            {
                Write(w, sound.Position);
                w.Write(sound.RadiusTiles);
                w.Write((byte)sound.Kind);
            }

            w.Write(state.YourNetId);
            w.Write(state.YourThreat);
            w.Write(state.YourInventory.Count);
            foreach (var itemId in state.YourInventory)
                w.Write(itemId);
        });

    public static StatePacket ReadState(BinaryReader r)
    {
        var tick = r.ReadUInt64();

        var actors = new ActorState[r.ReadInt32()];
        for (var i = 0; i < actors.Length; i++)
            actors[i] = new ActorState(r.ReadInt32(), r.ReadBoolean(), r.ReadSingle(),
                r.ReadSingle(), r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), ReadNullable(r));

        var doors = new DoorState[r.ReadInt32()];
        for (var i = 0; i < doors.Length; i++)
            doors[i] = new DoorState(ReadTilePos(r), r.ReadBoolean(), r.ReadBoolean());

        var items = new WorldItemState[r.ReadInt32()];
        for (var i = 0; i < items.Length; i++)
            items[i] = new WorldItemState(r.ReadString(), ReadTilePos(r));

        var changes = new TileChange[r.ReadInt32()];
        for (var i = 0; i < changes.Length; i++)
            changes[i] = new TileChange(ReadTilePos(r), r.ReadString(), r.ReadString());

        var sounds = new SoundCue[r.ReadInt32()];
        for (var i = 0; i < sounds.Length; i++)
            sounds[i] = new SoundCue(ReadTilePos(r), r.ReadSingle(), (SoundKind)r.ReadByte());

        var yourNetId = r.ReadInt32();
        var yourThreat = r.ReadSingle();
        var inventory = new string[r.ReadInt32()];
        for (var i = 0; i < inventory.Length; i++)
            inventory[i] = r.ReadString();

        return new StatePacket(tick, actors, doors, items, changes, sounds,
            yourNetId, yourThreat, inventory);
    }

    // ---- helpers ----

    /// <summary>Positions the reader on the payload, right after the type byte.</summary>
    public static BinaryReader OpenPayload(byte[] message)
    {
        var reader = new BinaryReader(new MemoryStream(message), Encoding.UTF8);
        reader.ReadByte();
        return reader;
    }

    private static byte[] Encode(MessageType type, Action<BinaryWriter> payload)
    {
        var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write((byte)type);
        payload(w);
        return stream.ToArray();
    }

    private static void Write(BinaryWriter w, TilePos pos)
    {
        w.Write(pos.X);
        w.Write(pos.Y);
        w.Write(pos.Floor);
    }

    private static TilePos ReadTilePos(BinaryReader r) =>
        new(r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

    private static void WriteNullable(BinaryWriter w, string? value)
    {
        w.Write(value is not null);
        if (value is not null)
            w.Write(value);
    }

    private static string? ReadNullable(BinaryReader r) =>
        r.ReadBoolean() ? r.ReadString() : null;
}
