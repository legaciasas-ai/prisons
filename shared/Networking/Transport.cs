namespace Prison.Shared.Networking;

/// <summary>
/// Server side of a message transport: discrete, reliable, ordered frames per peer.
/// Implementations queue network events internally; <see cref="Poll"/> dispatches them on
/// the caller's thread, so sessions stay single-threaded and deterministic (PLAN §7.11).
/// </summary>
public interface IServerTransport
{
    event Action<int>? PeerConnected;

    event Action<int>? PeerDisconnected;

    event Action<int, byte[]>? MessageReceived;

    void Send(int peerId, byte[] message);

    void Disconnect(int peerId);

    /// <summary>Dispatches queued connection/message events on the caller's thread.</summary>
    void Poll();
}

/// <summary>Client side of a message transport. Same threading contract as the server side.</summary>
public interface IClientTransport
{
    event Action? Connected;

    event Action? Disconnected;

    event Action<byte[]>? MessageReceived;

    void Send(byte[] message);

    void Disconnect();

    /// <summary>Dispatches queued connection/message events on the caller's thread.</summary>
    void Poll();
}

/// <summary>
/// In-memory transport pairing one server with any number of clients — the deterministic
/// backbone for tests and, later, the zero-copy path for a listen-server (host playing in
/// the same process as the authoritative simulation).
/// </summary>
public sealed class LoopbackTransport : IServerTransport
{
    private readonly Queue<int> _pendingConnects = [];
    private readonly Queue<int> _pendingDisconnects = [];
    private readonly Queue<(int Peer, byte[] Message)> _inbox = [];
    private readonly Dictionary<int, Client> _clients = [];
    private int _nextPeerId = 1;

    public event Action<int>? PeerConnected;

    public event Action<int>? PeerDisconnected;

    public event Action<int, byte[]>? MessageReceived;

    /// <summary>Creates a client endpoint; the server sees it connect on its next Poll.</summary>
    public IClientTransport CreateClient()
    {
        var client = new Client(this, _nextPeerId++);
        _clients[client.PeerId] = client;
        _pendingConnects.Enqueue(client.PeerId);
        return client;
    }

    public void Send(int peerId, byte[] message)
    {
        if (_clients.TryGetValue(peerId, out var client))
            client.Inbox.Enqueue(message);
    }

    public void Disconnect(int peerId)
    {
        if (!_clients.Remove(peerId, out var client))
            return;
        client.DisconnectedByServer = true;
        _pendingDisconnects.Enqueue(peerId);
    }

    public void Poll()
    {
        while (_pendingConnects.TryDequeue(out var peer))
            PeerConnected?.Invoke(peer);
        while (_inbox.TryDequeue(out var entry))
            MessageReceived?.Invoke(entry.Peer, entry.Message);
        while (_pendingDisconnects.TryDequeue(out var peer))
            PeerDisconnected?.Invoke(peer);
    }

    private sealed class Client(LoopbackTransport server, int peerId) : IClientTransport
    {
        public int PeerId { get; } = peerId;

        public Queue<byte[]> Inbox { get; } = [];

        public bool DisconnectedByServer { get; set; }

        private bool _connectedRaised;
        private bool _disconnectedRaised;

        public event Action? Connected;

        public event Action? Disconnected;

        public event Action<byte[]>? MessageReceived;

        public void Send(byte[] message)
        {
            if (!DisconnectedByServer)
                server._inbox.Enqueue((PeerId, message));
        }

        public void Disconnect()
        {
            if (server._clients.Remove(PeerId))
                server._pendingDisconnects.Enqueue(PeerId);
            DisconnectedByServer = true;
        }

        public void Poll()
        {
            if (!_connectedRaised)
            {
                _connectedRaised = true;
                Connected?.Invoke();
            }

            while (Inbox.TryDequeue(out var message))
                MessageReceived?.Invoke(message);

            if (DisconnectedByServer && !_disconnectedRaised)
            {
                _disconnectedRaised = true;
                Disconnected?.Invoke();
            }
        }
    }
}
