using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Prison.Shared.Networking;

/// <summary>
/// Length-prefixed TCP framing shared by both transport ends: <c>[length: int32 LE][payload]</c>.
/// TCP is the v1 wire (works identically in the plain .NET dedicated server, tests, and Godot);
/// an ENet/UDP transport can replace it behind the same interfaces later — see ADR 0002.
/// </summary>
internal static class TcpFraming
{
    /// <summary>Hard cap per frame; anything larger is a corrupt or hostile stream.</summary>
    public const int MaxFrameBytes = 8 * 1024 * 1024;

    public static async Task WriteFrameAsync(NetworkStream stream, byte[] payload, CancellationToken ct)
    {
        var header = BitConverter.GetBytes(payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    public static async Task<byte[]?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;
        var length = BitConverter.ToInt32(header);
        if (length is <= 0 or > MaxFrameBytes)
            return null;
        var payload = new byte[length];
        return await ReadExactAsync(stream, payload, ct).ConfigureAwait(false) ? payload : null;
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}

/// <summary>TCP listener for the headless dedicated server. Background tasks feed thread-safe
/// queues; <see cref="Poll"/> dispatches on the host loop's thread (single-threaded sessions).</summary>
public sealed class TcpServerTransport : IServerTransport, IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<int> _connects = new();
    private readonly ConcurrentQueue<int> _disconnects = new();
    private readonly ConcurrentQueue<(int Peer, byte[] Message)> _inbox = new();
    private readonly ConcurrentDictionary<int, PeerLink> _peers = new();
    private int _nextPeerId;

    private sealed record PeerLink(TcpClient Client, NetworkStream Stream, SemaphoreSlim SendLock);

    public event Action<int>? PeerConnected;

    public event Action<int>? PeerDisconnected;

    public event Action<int, byte[]>? MessageReceived;

    public TcpServerTransport(int port, IPAddress? bindAddress = null)
    {
        _listener = new TcpListener(bindAddress ?? IPAddress.Any, port);
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Send(int peerId, byte[] message)
    {
        if (_peers.TryGetValue(peerId, out var peer))
            _ = SendAsync(peerId, peer, message);
    }

    public void Disconnect(int peerId) => DropPeer(peerId);

    public void Poll()
    {
        while (_connects.TryDequeue(out var peer))
            PeerConnected?.Invoke(peer);
        while (_inbox.TryDequeue(out var entry))
            MessageReceived?.Invoke(entry.Peer, entry.Message);
        while (_disconnects.TryDequeue(out var peer))
            PeerDisconnected?.Invoke(peer);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        foreach (var peerId in _peers.Keys)
            DropPeer(peerId);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                client.NoDelay = true;
                var peerId = Interlocked.Increment(ref _nextPeerId);
                _peers[peerId] = new PeerLink(client, client.GetStream(), new SemaphoreSlim(1, 1));
                _connects.Enqueue(peerId);
                _ = ReadLoopAsync(peerId);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }
    }

    private async Task ReadLoopAsync(int peerId)
    {
        if (!_peers.TryGetValue(peerId, out var peer))
            return;
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await TcpFraming.ReadFrameAsync(peer.Stream, _cts.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                _inbox.Enqueue((peerId, frame));
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        DropPeer(peerId);
    }

    private async Task SendAsync(int peerId, PeerLink peer, byte[] message)
    {
        await peer.SendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await TcpFraming.WriteFrameAsync(peer.Stream, message, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            DropPeer(peerId);
        }
        finally
        {
            peer.SendLock.Release();
        }
    }

    private void DropPeer(int peerId)
    {
        if (!_peers.TryRemove(peerId, out var peer))
            return;
        peer.Client.Close();
        _disconnects.Enqueue(peerId);
    }
}

/// <summary>TCP client endpoint; usable from the Godot client and from tools/tests alike.</summary>
public sealed class TcpClientTransport : IClientTransport, IDisposable
{
    private readonly TcpClient _client = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private NetworkStream? _stream;
    private volatile bool _connectedPending;
    private volatile bool _disconnectedPending;
    private bool _connectedRaised;
    private bool _disconnectedRaised;

    public event Action? Connected;

    public event Action? Disconnected;

    public event Action<byte[]>? MessageReceived;

    public TcpClientTransport(string host, int port)
    {
        _client.NoDelay = true;
        _ = ConnectAsync(host, port);
    }

    public void Send(byte[] message)
    {
        if (_stream is { } stream)
            _ = SendAsync(stream, message);
    }

    public void Disconnect() => Drop();

    public void Poll()
    {
        if (_connectedPending && !_connectedRaised)
        {
            _connectedRaised = true;
            Connected?.Invoke();
        }
        while (_inbox.TryDequeue(out var message))
            MessageReceived?.Invoke(message);
        if (_disconnectedPending && !_disconnectedRaised)
        {
            _disconnectedRaised = true;
            Disconnected?.Invoke();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        Drop();
    }

    private async Task ConnectAsync(string host, int port)
    {
        try
        {
            await _client.ConnectAsync(host, port, _cts.Token).ConfigureAwait(false);
            _stream = _client.GetStream();
            _connectedPending = true;
            await ReadLoopAsync(_stream).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            Drop();
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await TcpFraming.ReadFrameAsync(stream, _cts.Token).ConfigureAwait(false);
                if (frame is null)
                    break;
                _inbox.Enqueue(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        Drop();
    }

    private async Task SendAsync(NetworkStream stream, byte[] message)
    {
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await TcpFraming.WriteFrameAsync(stream, message, _cts.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
            Drop();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void Drop()
    {
        _disconnectedPending = true;
        _client.Close();
    }
}
