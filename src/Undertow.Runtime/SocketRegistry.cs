using System.Collections.Concurrent;

namespace Undertow.Runtime;

/// <summary>
/// socketId → connection and topic → socketIds. Socket ids only above this
/// class — no WebSocket or SocketConnection appears in any signature above it,
/// and the topic map is explicitly local-only (the cluster seam).
/// </summary>
public sealed class SocketRegistry
{
    private readonly ConcurrentDictionary<string, SocketConnection> _sockets = new();
    private readonly Lock _topicsLock = new();
    private readonly Dictionary<string, HashSet<string>> _topics = [];

    public void Register(SocketConnection connection) => _sockets[connection.Id] = connection;

    public void Unregister(string socketId)
    {
        _sockets.TryRemove(socketId, out _);
        lock (_topicsLock)
        {
            foreach (var members in _topics.Values)
                members.Remove(socketId);
        }
    }

    public SocketConnection? Get(string socketId) =>
        _sockets.TryGetValue(socketId, out var connection) ? connection : null;

    public IReadOnlyList<SocketConnection> All() => [.. _sockets.Values];

    public void Subscribe(string topic, string socketId)
    {
        lock (_topicsLock)
        {
            if (!_topics.TryGetValue(topic, out var members))
                _topics[topic] = members = [];
            members.Add(socketId);
        }
    }

    public void Unsubscribe(string topic, string socketId)
    {
        lock (_topicsLock)
        {
            if (_topics.TryGetValue(topic, out var members))
            {
                members.Remove(socketId);
                if (members.Count == 0)
                    _topics.Remove(topic);
            }
        }
    }

    public string[] Subscribers(string topic)
    {
        lock (_topicsLock)
        {
            return _topics.TryGetValue(topic, out var members) ? [.. members] : [];
        }
    }
}

/// <summary>Topic fan-out and per-socket push. Encoding is per socket — the
/// transports frame differently; payload bytes are shared, only the wrapper is
/// per socket. TryEnqueue never blocks, so calls are safe under document locks.</summary>
public interface IChannelBroadcaster
{
    void Broadcast(string topic, string @event, ReadOnlyMemory<byte> payload);
    void BroadcastFrom(string exceptSocketId, string topic, string @event, ReadOnlyMemory<byte> payload);
    void Push(string socketId, string topic, string @event, ReadOnlyMemory<byte> payload);
}

public sealed class LocalBroadcaster(SocketRegistry registry) : IChannelBroadcaster
{
    public void Broadcast(string topic, string @event, ReadOnlyMemory<byte> payload)
    {
        foreach (var socketId in registry.Subscribers(topic))
            Push(socketId, topic, @event, payload);
    }

    public void BroadcastFrom(string exceptSocketId, string topic, string @event, ReadOnlyMemory<byte> payload)
    {
        foreach (var socketId in registry.Subscribers(topic))
        {
            if (socketId != exceptSocketId)
                Push(socketId, topic, @event, payload);
        }
    }

    public void Push(string socketId, string topic, string @event, ReadOnlyMemory<byte> payload)
    {
        if (registry.Get(socketId) is { } connection)
            connection.TryEnqueue(connection.Framing.Push(topic, @event, payload));
    }
}
