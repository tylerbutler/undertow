using System.Net.WebSockets;
using System.Text;

namespace Undertow.WireDiff;

/// <summary>Thread-safe transcript: "&gt; " sent, "&lt; " received, "# " annotation.</summary>
internal sealed class Transcript
{
    private readonly List<string> _lines = [];

    internal void Add(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    internal string[] Snapshot()
    {
        lock (_lines)
        {
            return [.. _lines];
        }
    }

    internal int Count
    {
        get
        {
            lock (_lines)
            {
                return _lines.Count;
            }
        }
    }
}

/// <summary>
/// Records raw text frames on one WebSocket. A background pump receives
/// continuously (cancelling a ClientWebSocket receive aborts the socket, so
/// waiting is done against the transcript, never against the socket).
/// </summary>
internal sealed class SocketRecorder : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Transcript _log;
    private readonly string _label;
    private Task? _pump;

    internal SocketRecorder(Transcript log, string label = "")
    {
        _log = log;
        _label = label;
    }

    internal async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        _log.Add($"# connect {uri.PathAndQuery}");
        await _ws.ConnectAsync(uri, ct);
        _pump = Task.Run(PumpAsync, CancellationToken.None);
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[1 << 20];
        var filled = 0;
        try
        {
            while (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseSent)
            {
                var result = await _ws.ReceiveAsync(buffer.AsMemory(filled), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _log.Add($"# server closed: {(int?)_ws.CloseStatus} {_ws.CloseStatusDescription}");
                    return;
                }

                filled += result.Count;
                if (result.EndOfMessage)
                {
                    _log.Add($"<{_label} {Encoding.UTF8.GetString(buffer, 0, filled)}");
                    filled = 0;
                }
            }
        }
        catch (WebSocketException e)
        {
            _log.Add($"# socket error: {e.Message}");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task SendAsync(string frame, CancellationToken ct)
    {
        _log.Add($">{_label} {frame}");
        await _ws.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Wait until a received frame contains <paramref name="marker"/>; null on timeout.</summary>
    internal async Task<string?> WaitForAsync(string marker, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hit = _log.Snapshot()
                .LastOrDefault(l => l.StartsWith($"<{_label} ", StringComparison.Ordinal) &&
                                    l.Contains(marker, StringComparison.Ordinal));
            if (hit is not null)
                return hit;
            await Task.Delay(50);
        }

        _log.Add($"# timeout waiting for {marker}");
        return null;
    }

    /// <summary>Wait until the transcript has been quiet for <paramref name="quiet"/>.</summary>
    internal async Task SettleAsync(TimeSpan quiet)
    {
        var count = _log.Count;
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow - quietSince < quiet)
        {
            await Task.Delay(50);
            var now = _log.Count;
            if (now != count)
            {
                count = now;
                quietSince = DateTime.UtcNow;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
            // Recording is finished; a failed close handshake is not part of the transcript.
        }

        _ws.Dispose();
        if (_pump is not null)
            await _pump;
    }
}
