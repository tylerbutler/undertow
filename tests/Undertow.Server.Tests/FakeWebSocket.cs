using System.Net.WebSockets;
using System.Text;

namespace Undertow.Server.Tests;

/// <summary>A WebSocket stub that records sent frames; receives block forever.</summary>
public sealed class FakeWebSocket : WebSocket
{
    private readonly List<string> _sent = [];
    private WebSocketState _state = WebSocketState.Open;

    public IReadOnlyList<string> SentFrames
    {
        get
        {
            lock (_sent)
            {
                return [.. _sent];
            }
        }
    }

    public bool Aborted { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => null;
    public override string? CloseStatusDescription => null;
    public override WebSocketState State => _state;
    public override string? SubProtocol => null;

    public override void Abort()
    {
        Aborted = true;
        _state = WebSocketState.Aborted;
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        _state = WebSocketState.CloseSent;
        return Task.CompletedTask;
    }

    public override void Dispose() => _state = WebSocketState.Closed;

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new OperationCanceledException(cancellationToken);
    }

    public override Task SendAsync(
        ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
        CancellationToken cancellationToken)
    {
        lock (_sent)
        {
            _sent.Add(Encoding.UTF8.GetString(buffer.Array!, buffer.Offset, buffer.Count));
        }

        return Task.CompletedTask;
    }
}
