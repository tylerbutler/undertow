using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Undertow.Protocol;
using Undertow.Runtime;

namespace Undertow.Transports;

/// <summary>
/// Socket.IO event framing (`42[...]`). op and nack are two-argument frames
/// carrying the documentId (derived from the topic's third segment); everything
/// else is one-argument — enforced here, as socketio.gleam does by signature.
/// </summary>
public sealed class SocketIoFraming : ISocketFraming
{
    public static readonly SocketIoFraming Instance = new();

    public ReadOnlyMemory<byte> Push(string topic, string @event, ReadOnlyMemory<byte> payload)
    {
        var buffer = new ArrayBufferWriter<byte>(24 + payload.Length);
        buffer.Write("42["u8);
        WriteJsonString(buffer, @event);
        if (@event is FluidEvents.Op or FluidEvents.Nack)
        {
            buffer.Write(","u8);
            WriteJsonString(buffer, DocumentId(topic));
        }

        buffer.Write(","u8);
        buffer.Write(payload.Span);
        buffer.Write("]"u8);
        return buffer.WrittenMemory;
    }

    /// <summary>Graceful channel termination: the bare `42["close"]`.</summary>
    public ReadOnlyMemory<byte>? Close(string topic, string joinRef) => "42[\"close\"]"u8.ToArray();

    private static string DocumentId(string topic) =>
        topic.Split(':') is ["document", _, var documentId] ? documentId : topic;

    private static void WriteJsonString(ArrayBufferWriter<byte> buffer, string value)
    {
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            SkipValidation = true,
        });
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// The /socket.io/ endpoint: Engine.IO v4 open handshake, 25 s ping timer with
/// the 45 s (interval + timeout) pong deadline, origin policy 403 before
/// upgrade, sticky per-socket topic, and positional-args translation.
/// </summary>
public sealed class SocketIoTransport(
    ChannelDispatcher dispatcher,
    SocketRegistry registry,
    OriginPolicyBox originPolicy,
    TransportGuards guards,
    long maxFrameBytes,
    TimeProvider time)
{
    public const int PingIntervalMs = 25_000;
    public const int PingTimeoutMs = 20_000;

    public static bool Matches(PathString path) => path == "/socket.io" || path == "/socket.io/";

    public async Task HandleAsync(HttpContext context)
    {
        // Reject cross-site browser upgrades before the handshake; clients
        // sending no Origin (the official Fluid drivers) stay admitted under
        // the default policy. EIO/transport query params are ignored, as the
        // Gleam transport ignores them.
        var origin = context.Request.Headers.Origin.Count > 0 ? context.Request.Headers.Origin.ToString() : null;
        var host = context.Request.Headers.Host.Count > 0 ? context.Request.Headers.Host.ToString() : null;
        if (!originPolicy.Allowed(origin, host))
        {
            context.Response.StatusCode = 403;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 404;
            return;
        }

        // Slot acquired before the upgrade — rejection is a 429 status, not a
        // close frame. The real peer address, never X-Forwarded-For.
        var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!guards.Connections.TryAcquire(ip))
        {
            context.Response.StatusCode = 429;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            // The Fluid clientId IS the Engine.IO sid: 32 uppercase hex.
            var socketId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var connection = new SocketConnection(socketId, socket, SocketIoFraming.Instance, time);
            registry.Register(connection);

            connection.TryEnqueue(EncodeOpen(socketId));
            var pinger = Task.Run(() => PingLoopAsync(connection));

            try
            {
                await ReadLoopAsync(connection, socket, context.RequestAborted);
            }
            finally
            {
                await dispatcher.TerminateAllAsync(connection, sendClose: false);
                registry.Unregister(socketId);
                await connection.DisposeAsync();
                await pinger;
            }
        }
        finally
        {
            guards.Connections.Release(ip);
        }
    }

    private byte[] EncodeOpen(string sid) => EncodeOpenFrame(sid, maxFrameBytes);

    /// <summary>The Engine.IO open packet. maxPayload is the configured frame
    /// cap — the same value enforced per frame and advertised in IConnected.</summary>
    public static byte[] EncodeOpenFrame(string sid, long maxPayload) =>
        Encoding.UTF8.GetBytes(
            $$"""0{"sid":"{{sid}}","upgrades":[],"pingInterval":{{PingIntervalMs}},"pingTimeout":{{PingTimeoutMs}},"maxPayload":{{maxPayload}}}""");

    /// <summary>Whether the peer has gone silent past the advertised
    /// pingTimeout. The allowance is interval + timeout because the deadline
    /// is evaluated on the interval tick — a pong arriving just before one
    /// tick must not be judged stale at the next.</summary>
    public static bool PongOverdue(TimeProvider time, long lastInboundTimestamp) =>
        time.GetElapsedTime(lastInboundTimestamp).TotalMilliseconds > PingIntervalMs + PingTimeoutMs;

    /// <summary>
    /// One timer serves both jobs: send the ping, and enforce the pingTimeout
    /// the handshake advertises. The allowance is interval + timeout because
    /// the deadline is evaluated on the tick — a pong arriving just before one
    /// tick must not be judged stale at the next.
    /// </summary>
    private async Task PingLoopAsync(SocketConnection connection)
    {
        try
        {
            while (!connection.Closed.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(PingIntervalMs), time, connection.Closed);
                if (PongOverdue(time, Volatile.Read(ref connection.LastInboundTimestamp)))
                {
                    connection.Abort(WebSocketCloseStatus.EndpointUnavailable, "ping timeout");
                    return;
                }

                connection.TryEnqueue("2"u8.ToArray());
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ReadLoopAsync(SocketConnection connection, WebSocket socket, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connection.Closed);
        var stickyTopic = "";
        var messageBucket = guards.NewMessageBucket();
        var joinBucket = guards.NewJoinBucket();
        while (socket.State == WebSocketState.Open)
        {
            var frame = await FrameReader.ReadAsync(socket, maxFrameBytes, linked.Token);
            if (frame.Closed)
                return;

            if (frame.TooLarge)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large", cts.Token);
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException)
                {
                }

                return;
            }

            Volatile.Write(ref connection.LastInboundTimestamp, time.GetTimestamp());

            // Over-budget frames are dropped, matching the Gleam transport.
            if (!messageBucket.TryTake())
                continue;

            // Binary frames have no Engine.IO meaning here; ignore.
            if (frame.Binary || frame.Text is null)
                continue;

            stickyTopic = await DispatchAsync(connection, frame.Text, stickyTopic, joinBucket);
        }
    }

    /// <summary>Classify and dispatch one text frame; returns the (possibly
    /// updated) sticky topic. Unrecognized frames are ignored silently.</summary>
    private async Task<string> DispatchAsync(
        SocketConnection connection, byte[] text, string stickyTopic, TokenBucket joinBucket)
    {
        if (text.Length == 1 && text[0] == (byte)'2')
        {
            // Engine.IO ping: pong back; the inbound frame already refreshed
            // the liveness clock.
            connection.TryEnqueue("3"u8.ToArray());
            return stickyTopic;
        }

        if (text.Length == 1 && text[0] == (byte)'3')
            return stickyTopic; // pong: liveness only

        if (text.Length == 2 && text[0] == (byte)'4' && text[1] == (byte)'0')
        {
            connection.TryEnqueue(Encoding.UTF8.GetBytes($$"""40{"sid":"{{connection.Id}}"}"""));
            return stickyTopic;
        }

        if (text.Length < 2 || text[0] != (byte)'4' || text[1] != (byte)'2')
            return stickyTopic;

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(text.AsMemory(2));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 ||
                root[0].ValueKind != JsonValueKind.String)
            {
                return stickyTopic;
            }

            var @event = root[0].GetString()!;
            var argCount = root.GetArrayLength() - 1;

            if (@event == FluidEvents.ConnectDocument && argCount >= 1)
            {
                var payload = root[1];
                var tenant = StringField(payload, "tenantId");
                var documentId = StringField(payload, "id");
                if (tenant.Length == 0 || documentId.Length == 0)
                    return stickyTopic;

                if (!joinBucket.TryTake())
                    return stickyTopic;

                var topic = $"document:{tenant}:{documentId}";
                var outcome = await dispatcher.JoinAsync(connection, joinRef: "", topic, payload);
                var replyEvent = outcome.Ok ? FluidEvents.ConnectDocumentSuccess : FluidEvents.ConnectDocumentError;
                connection.TryEnqueue(EncodeEvent(replyEvent, outcome.Reply));
                return topic;
            }

            if (@event == FluidEvents.SubmitOp && argCount >= 2 && stickyTopic.Length > 0)
            {
                await DispatchTranslatedAsync(
                    connection, stickyTopic, @event, "messageBatches", root[1], root[2]);
                return stickyTopic;
            }

            if (@event == FluidEvents.SubmitSignal && argCount >= 2 && stickyTopic.Length > 0)
            {
                await DispatchTranslatedAsync(connection, stickyTopic, @event, "signals", root[1], root[2]);
                return stickyTopic;
            }

            if (argCount >= 1 && stickyTopic.Length > 0)
            {
                var outcome = await dispatcher.HandleEventAsync(connection, stickyTopic, @event, root[1]);
                EnqueueOutcome(connection, stickyTopic, outcome);
                return stickyTopic;
            }

            return stickyTopic;
        }
        catch (JsonException)
        {
            return stickyTopic;
        }
        finally
        {
            document?.Dispose();
        }
    }

    /// <summary>Positional args → payload object ({clientId, <key>: value}).</summary>
    private async Task DispatchTranslatedAsync(
        SocketConnection connection, string topic, string @event, string key,
        JsonElement clientId, JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("clientId");
            clientId.WriteTo(writer);
            writer.WritePropertyName(key);
            value.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var translated = JsonDocument.Parse(buffer.WrittenMemory);
        var outcome = await dispatcher.HandleEventAsync(connection, topic, @event, translated.RootElement);
        EnqueueOutcome(connection, topic, outcome);
    }

    private static void EnqueueOutcome(SocketConnection connection, string topic, HandleOutcome outcome)
    {
        if (outcome.PushEvent is not null)
            connection.TryEnqueue(connection.Framing.Push(topic, outcome.PushEvent, outcome.PushPayload));
    }

    private static byte[] EncodeEvent(string @event, ReadOnlyMemory<byte> payload)
    {
        var buffer = new ArrayBufferWriter<byte>(16 + payload.Length);
        buffer.Write("42[\""u8);
        buffer.Write(Encoding.UTF8.GetBytes(@event));
        buffer.Write("\","u8);
        if (payload.Length == 0)
        {
            buffer.Write("{}"u8);
        }
        else
        {
            buffer.Write(payload.Span);
        }

        buffer.Write("]"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private static string StringField(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : "";
}
