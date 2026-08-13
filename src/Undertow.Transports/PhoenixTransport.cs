using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Undertow.Protocol;
using Undertow.Runtime;

namespace Undertow.Transports;

/// <summary>Phoenix Channels V2 framing: [join_ref, ref, topic, event, payload].</summary>
public sealed class PhoenixFraming : ISocketFraming
{
    public static readonly PhoenixFraming Instance = new();

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = true,
    };

    /// <summary>Pushes carry null join_ref and ref.</summary>
    public ReadOnlyMemory<byte> Push(string topic, string @event, ReadOnlyMemory<byte> payload) =>
        Encode(null, null, topic, @event, payload);

    /// <summary>phx_close mirrors the join_ref into the ref slot.</summary>
    public ReadOnlyMemory<byte>? Close(string topic, string joinRef) =>
        Encode(joinRef, joinRef, topic, "phx_close", "{}"u8.ToArray());

    public ReadOnlyMemory<byte> Reply(
        string? joinRef, string? @ref, string topic, bool ok, ReadOnlyMemory<byte> response)
    {
        var buffer = new ArrayBufferWriter<byte>(128 + response.Length);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            WriteRef(writer, joinRef);
            WriteRef(writer, @ref);
            writer.WriteStringValue(topic);
            writer.WriteStringValue("phx_reply");
            writer.WriteStartObject();
            writer.WriteString("status", ok ? "ok" : "error");
            writer.WritePropertyName("response");
            if (response.Length == 0)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteRawValue(response.Span, skipInputValidation: true);
            }

            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return buffer.WrittenMemory;
    }

    public static ReadOnlyMemory<byte> Encode(
        string? joinRef, string? @ref, string topic, string @event, ReadOnlyMemory<byte> payload)
    {
        var buffer = new ArrayBufferWriter<byte>(64 + payload.Length);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartArray();
            WriteRef(writer, joinRef);
            WriteRef(writer, @ref);
            writer.WriteStringValue(topic);
            writer.WriteStringValue(@event);
            if (payload.Length == 0)
            {
                writer.WriteStartObject();
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteRawValue(payload.Span, skipInputValidation: true);
            }

            writer.WriteEndArray();
        }

        return buffer.WrittenMemory;
    }

    private static void WriteRef(Utf8JsonWriter writer, string? value)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}

/// <summary>
/// The /socket/websocket endpoint: vsn gate (400 before upgrade), origin/CSWSH
/// policy (403 before upgrade — never ASP.NET CORS, which does not govern
/// WebSockets), then the V2 frame loop.
/// </summary>
public sealed class PhoenixTransport(
    ChannelDispatcher dispatcher,
    SocketRegistry registry,
    OriginPolicyBox originPolicy,
    TransportGuards guards,
    long maxFrameBytes,
    TimeProvider time)
{
    public const string Path = "/socket/websocket";

    public async Task HandleAsync(HttpContext context)
    {
        // vsn gate: the V2 serializer is the only one spoken here. 403, not
        // 400 — the Phase-0 fixture (phoenix-bad-vsn.txt) pins the Gleam
        // server's actual status.
        var vsn = context.Request.Query["vsn"].ToString();
        if (!vsn.StartsWith("2.", StringComparison.Ordinal))
        {
            context.Response.StatusCode = 403;
            return;
        }

        var origin = context.Request.Headers.Origin.Count > 0 ? context.Request.Headers.Origin.ToString() : null;
        var host = context.Request.Headers.Host.Count > 0 ? context.Request.Headers.Host.ToString() : null;
        if (!originPolicy.Allowed(origin, host))
        {
            context.Response.StatusCode = 403;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            return;
        }

        // The real socket peer address, deliberately not X-Forwarded-For (a
        // client sets that freely). Acquired before the upgrade so rejection
        // is a status code, not a close frame.
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!guards.Connections.TryAcquire(ip))
        {
            context.Response.StatusCode = 429;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            // The Fluid client id IS the socket id: 32 uppercase hex.
            var socketId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var connection = new SocketConnection(socketId, socket, PhoenixFraming.Instance, time);
            registry.Register(connection);

            try
            {
                await ReadLoopAsync(connection, socket, context.RequestAborted);
            }
            finally
            {
                await dispatcher.TerminateAllAsync(connection, sendClose: false);
                registry.Unregister(socketId);
                await connection.DisposeAsync();
            }
        }
        finally
        {
            guards.Connections.Release(ip);
        }
    }

    private async Task ReadLoopAsync(SocketConnection connection, WebSocket socket, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closed);
        var messageBucket = guards.NewMessageBucket();
        var joinBucket = guards.NewJoinBucket();
        while (socket.State == WebSocketState.Open)
        {
            var frame = await FrameReader.ReadAsync(socket, maxFrameBytes, linked.Token);
            if (frame.Closed)
                return;

            if (frame.TooLarge)
            {
                await TryCloseAsync(socket, WebSocketCloseStatus.MessageTooBig, "frame too large");
                return;
            }

            connection.LastInboundTimestamp = time.GetTimestamp();

            // Over-budget frames are dropped, matching the Gleam transport.
            if (!messageBucket.TryTake())
                continue;

            // Binary frames: accept and ignore (levee-driver never sends them).
            if (frame.Binary || frame.Text is null)
                continue;

            await DispatchAsync(connection, frame.Text, joinBucket);
        }
    }

    private async Task DispatchAsync(SocketConnection connection, byte[] text, TokenBucket joinBucket)
    {
        string? joinRef = null, @ref = null, topic = null, @event = null;
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() != 5)
            {
                return;
            }

            var root = document.RootElement;
            joinRef = root[0].ValueKind == JsonValueKind.String ? root[0].GetString() : null;
            @ref = root[1].ValueKind == JsonValueKind.String ? root[1].GetString() : null;
            topic = root[2].GetString();
            @event = root[3].GetString();
            var payload = root[4];
            if (topic is null || @event is null)
                return;

            switch (@event)
            {
                case "phx_join":
                    if (!joinBucket.TryTake())
                        break;
                    var outcome = await dispatcher.JoinAsync(connection, joinRef ?? "", topic, payload);
                    var response = outcome.HasReply ? outcome.Reply : ReadOnlyMemory<byte>.Empty;
                    connection.TryEnqueue(PhoenixFraming.Instance.Reply(joinRef, @ref, topic, outcome.Ok, response));
                    break;

                case "phx_leave":
                    connection.TryEnqueue(
                        PhoenixFraming.Instance.Reply(joinRef, @ref, topic, ok: true, ReadOnlyMemory<byte>.Empty));
                    await dispatcher.TerminateAsync(connection, topic, sendClose: true);
                    break;

                case "heartbeat" when topic == "phoenix":
                    connection.TryEnqueue(
                        PhoenixFraming.Instance.Reply(joinRef, @ref, topic, ok: true, ReadOnlyMemory<byte>.Empty));
                    break;

                default:
                    var handled = await dispatcher.HandleEventAsync(connection, topic, @event, payload);
                    if (handled.PushEvent is not null)
                    {
                        connection.TryEnqueue(
                            connection.Framing.Push(topic, handled.PushEvent, handled.PushPayload));
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // Undecodable frame: ignore, matching the reference transports.
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static async Task TryCloseAsync(WebSocket socket, WebSocketCloseStatus status, string reason)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.CloseAsync(status, reason, cts.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }
}
