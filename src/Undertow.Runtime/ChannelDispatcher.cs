using System.Text.Json;

namespace Undertow.Runtime;

public sealed record JoinOutcome(bool Ok, ReadOnlyMemory<byte> Reply, bool HasReply, object? Assigns);

/// <summary>What a handler wants sent back on the same socket (if anything).</summary>
public sealed record HandleOutcome(string? PushEvent, ReadOnlyMemory<byte> PushPayload)
{
    public static readonly HandleOutcome NoReply = new(null, ReadOnlyMemory<byte>.Empty);
    public static HandleOutcome Push(string @event, byte[] payload) => new(@event, payload);
}

public sealed class ChannelContext(string socketId, string topic, ChannelInstance instance, IChannelBroadcaster broadcaster)
{
    public string SocketId { get; } = socketId;
    public string Topic { get; } = topic;
    public IChannelBroadcaster Broadcaster { get; } = broadcaster;

    public object? Assigns
    {
        get => instance.Assigns;
        set => instance.Assigns = value;
    }
}

public interface IChannelHandler
{
    ValueTask<JoinOutcome> JoinAsync(ChannelContext context, JsonElement payload);
    ValueTask<HandleOutcome> HandleInAsync(ChannelContext context, string @event, JsonElement payload);
    ValueTask TerminateAsync(ChannelContext context);
}

/// <summary>
/// Join / handle_in / terminate dispatch with the two beryl behaviours that
/// must not be dropped: duplicate join replaces (terminating the old instance
/// first, which for mode:"write" emits a sequenced leave op), and
/// client-supplied phx_* events never reach the handler.
/// </summary>
public sealed class ChannelDispatcher(SocketRegistry registry, IChannelBroadcaster broadcaster, IChannelHandler handler)
{
    public IChannelBroadcaster Broadcaster => broadcaster;

    public async ValueTask<JoinOutcome> JoinAsync(
        SocketConnection connection, string joinRef, string topic, JsonElement payload)
    {
        // Duplicate join replaces: terminate the old membership first.
        if (connection.Channels.TryGetValue(topic, out var existing))
            await TerminateAsync(connection, topic, sendClose: false);

        var instance = new ChannelInstance(topic, joinRef);
        var context = new ChannelContext(connection.Id, topic, instance, broadcaster);

        JoinOutcome outcome;
        try
        {
            outcome = await handler.JoinAsync(context, payload);
        }
        catch (Exception)
        {
            // Crash isolation: a malformed payload takes down this join, never
            // the dispatcher or the socket's other channels.
            return new JoinOutcome(false, "{\"reason\":\"internal error\"}"u8.ToArray(), true, null);
        }

        if (outcome.Ok)
        {
            instance.Assigns = outcome.Assigns;
            connection.Channels[topic] = instance;
            registry.Subscribe(topic, connection.Id);
        }

        return outcome;
    }

    public async ValueTask<HandleOutcome> HandleEventAsync(
        SocketConnection connection, string topic, string @event, JsonElement payload)
    {
        // Reject client-supplied phx_* control events before the handler.
        if (@event.StartsWith("phx_", StringComparison.Ordinal))
            return HandleOutcome.NoReply;

        if (!connection.Channels.TryGetValue(topic, out var instance))
            return HandleOutcome.NoReply;

        var context = new ChannelContext(connection.Id, topic, instance, broadcaster);
        try
        {
            return await handler.HandleInAsync(context, @event, payload);
        }
        catch (Exception)
        {
            return HandleOutcome.NoReply;
        }
    }

    /// <summary>Terminate one channel membership: run the handler's leave path,
    /// drop the subscription, and (optionally) send the close frame.</summary>
    public async ValueTask TerminateAsync(SocketConnection connection, string topic, bool sendClose)
    {
        if (!connection.Channels.TryRemove(topic, out var instance))
            return;

        registry.Unsubscribe(topic, connection.Id);
        var context = new ChannelContext(connection.Id, topic, instance, broadcaster);
        try
        {
            await handler.TerminateAsync(context);
        }
        catch (Exception)
        {
            // Terminate must always complete teardown.
        }

        if (sendClose && connection.Framing.Close(topic, instance.JoinRef) is { } closeFrame)
            connection.TryEnqueue(closeFrame);
    }

    /// <summary>Terminate every membership (socket teardown or eviction).</summary>
    public async ValueTask TerminateAllAsync(SocketConnection connection, bool sendClose)
    {
        foreach (var topic in connection.Channels.Keys.ToArray())
            await TerminateAsync(connection, topic, sendClose);
    }
}
