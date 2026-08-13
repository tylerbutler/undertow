using System.Buffers;
using System.Net.WebSockets;

namespace Undertow.Transports;

/// <summary>
/// Reassembles fragmented WebSocket messages (Kestrel, unlike mist, does not).
/// A 16 MB op arrives as thousands of fragments; accumulate with a hard cap
/// and close 1009 on breach.
/// </summary>
public static class FrameReader
{
    public sealed record Frame(byte[]? Text, bool Closed, bool TooLarge, bool Binary);

    public static async Task<Frame> ReadAsync(WebSocket socket, long maxFrameBytes, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(16384);
        var binary = false;
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer.GetMemory(16384), ct);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                return new Frame(null, Closed: true, TooLarge: false, Binary: false);
            }

            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Complete the close handshake so the peer sees a clean 1000
                // rather than an aborted connection.
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException
                                              or ObjectDisposedException or InvalidOperationException)
                {
                    // A concurrent outbound send can race the close; the peer
                    // is gone either way.
                }

                return new Frame(null, Closed: true, TooLarge: false, Binary: false);
            }

            binary |= result.MessageType == WebSocketMessageType.Binary;
            buffer.Advance(result.Count);
            if (buffer.WrittenCount > maxFrameBytes)
                return new Frame(null, Closed: false, TooLarge: true, Binary: binary);

            if (result.EndOfMessage)
                return new Frame(buffer.WrittenSpan.ToArray(), Closed: false, TooLarge: false, Binary: binary);
        }
    }
}
