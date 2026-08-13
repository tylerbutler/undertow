using System.Net.WebSockets;
using System.Threading.Channels;

namespace Undertow.Runtime;

/// <summary>Per-socket wire framing: the two transports frame pushes differently.</summary>
public interface ISocketFraming
{
    /// <summary>A server-initiated push of an event on a topic.</summary>
    ReadOnlyMemory<byte> Push(string topic, string @event, ReadOnlyMemory<byte> payload);

    /// <summary>The channel-terminated frame (phx_close / 42["close"]).</summary>
    ReadOnlyMemory<byte>? Close(string topic, string joinRef);
}

/// <summary>One channel membership on a socket.</summary>
public sealed class ChannelInstance(string topic, string joinRef)
{
    public string Topic { get; } = topic;
    public string JoinRef { get; } = joinRef;
    public object? Assigns { get; set; }
}

/// <summary>
/// One live WebSocket: identity, bounded outbound queue + single pump task,
/// per-socket framing, and this socket's channel instances. WebSocket.SendAsync
/// is not concurrency-safe — everything outbound goes through the queue.
/// </summary>
public sealed class SocketConnection : IAsyncDisposable
{
    /// <summary>Bounded queue: one wedged consumer must not OOM the server.</summary>
    public const int OutboundCapacity = 1024;

    private readonly WebSocket _socket;
    private readonly Channel<ReadOnlyMemory<byte>> _outbound;
    private readonly CancellationTokenSource _closeCts = new();
    private readonly Task _pump;
    private int _dropped;

    public string Id { get; }
    public ISocketFraming Framing { get; }

    /// <summary>topic → channel instance. Socket.IO uses one; Phoenix may join several.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, ChannelInstance> Channels { get; } = new();

    /// <summary>Ticks (TimeProvider timestamp) of the last inbound frame — any
    /// frame, not just heartbeats — for the liveness sweep.</summary>
    public long LastInboundTimestamp;

    public SocketConnection(string id, WebSocket socket, ISocketFraming framing, TimeProvider time)
    {
        Id = id;
        _socket = socket;
        Framing = framing;
        LastInboundTimestamp = time.GetTimestamp();
        _outbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(
            new BoundedChannelOptions(OutboundCapacity)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
            });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Token cancelled when the connection is being torn down; the
    /// transport read loop and the sweep both observe it.</summary>
    public CancellationToken Closed => _closeCts.Token;

    /// <summary>
    /// Non-blocking enqueue (safe under a document lock). On a full queue the
    /// frame is dropped and the socket marked for eviction: Fluid clients
    /// reconnect and catch up via requestOps, so eviction is cheap and correct.
    /// </summary>
    public void TryEnqueue(ReadOnlyMemory<byte> frame)
    {
        if (!_outbound.Writer.TryWrite(frame) && Interlocked.Exchange(ref _dropped, 1) == 0)
            Abort(WebSocketCloseStatus.InternalServerError, "outbound overflow");
    }

    /// <summary>Whether a write was ever dropped (slow consumer).</summary>
    public bool Overflowed => Volatile.Read(ref _dropped) == 1;

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(_closeCts.Token))
            {
                await _socket.SendAsync(frame, WebSocketMessageType.Text, true, _closeCts.Token);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or WebSocketException or ObjectDisposedException)
        {
            // Socket went away; teardown owns cleanup.
        }
    }

    /// <summary>Actively close from the server side (sweep eviction, overflow).
    /// This is the register_closer equivalent: an evicted socket is really
    /// closed, not left a zombie whose frames are silently dropped.</summary>
    public void Abort(WebSocketCloseStatus status, string description)
    {
        try
        {
            _closeCts.Cancel();
            _socket.Abort();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();
        if (!_closeCts.IsCancellationRequested)
            _closeCts.Cancel();

        try
        {
            await _pump;
        }
        catch (OperationCanceledException)
        {
        }

        _closeCts.Dispose();
    }
}
