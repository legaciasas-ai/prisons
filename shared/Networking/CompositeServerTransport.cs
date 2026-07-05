namespace Prison.Shared.Networking;

/// <summary>
/// Merges several server transports behind one <see cref="IServerTransport"/> — the
/// player-hosted listen-server case (PLAN §10.1): the host plays over an in-process
/// <see cref="LoopbackTransport"/> while friends join over TCP, and the authoritative
/// <see cref="ServerSession"/> sees one uniform peer list. Inner peer ids are remapped to
/// composite-wide ids so transports can't collide.
/// </summary>
public sealed class CompositeServerTransport : IServerTransport, IDisposable
{
    private readonly IServerTransport[] _inner;
    private readonly Dictionary<(IServerTransport Transport, int InnerId), int> _outerOf = [];
    private readonly Dictionary<int, (IServerTransport Transport, int InnerId)> _innerOf = [];
    private int _nextOuterId = 1;

    public event Action<int>? PeerConnected;

    public event Action<int>? PeerDisconnected;

    public event Action<int, byte[]>? MessageReceived;

    public CompositeServerTransport(params IServerTransport[] inner)
    {
        _inner = inner;
        foreach (var transport in inner)
        {
            transport.PeerConnected += innerId =>
            {
                var outerId = _nextOuterId++;
                _outerOf[(transport, innerId)] = outerId;
                _innerOf[outerId] = (transport, innerId);
                PeerConnected?.Invoke(outerId);
            };
            transport.MessageReceived += (innerId, message) =>
            {
                if (_outerOf.TryGetValue((transport, innerId), out var outerId))
                    MessageReceived?.Invoke(outerId, message);
            };
            transport.PeerDisconnected += innerId =>
            {
                if (!_outerOf.Remove((transport, innerId), out var outerId))
                    return;
                _innerOf.Remove(outerId);
                PeerDisconnected?.Invoke(outerId);
            };
        }
    }

    public void Send(int peerId, byte[] message)
    {
        if (_innerOf.TryGetValue(peerId, out var peer))
            peer.Transport.Send(peer.InnerId, message);
    }

    public void Disconnect(int peerId)
    {
        if (_innerOf.TryGetValue(peerId, out var peer))
            peer.Transport.Disconnect(peer.InnerId);
    }

    public void Poll()
    {
        foreach (var transport in _inner)
            transport.Poll();
    }

    public void Dispose()
    {
        foreach (var transport in _inner)
            (transport as IDisposable)?.Dispose();
    }
}
