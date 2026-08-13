using System.Collections.Concurrent;
using Undertow.Abstractions;

namespace Undertow.Runtime;

/// <summary>
/// Topic → session map. The Lazy gives exactly-once async rehydration when two
/// connects race a cold document. Read paths use <see cref="TryGet"/>, which
/// never runs the factory — `exists` is reachable from REST paths that must
/// not allocate a session for an unknown id.
/// </summary>
public sealed class DocumentRegistry(
    IDocumentStore store, IGitObjectStore gitObjects, TimeProvider time, bool compatRestoreMsnFromSummary,
    bool pruneOpsBelowSummary = false)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<DocumentSession>>> _sessions = new();

    public Task<DocumentSession> GetOrCreateAsync(string topic) =>
        _sessions.GetOrAdd(topic, t => new Lazy<Task<DocumentSession>>(() =>
            DocumentSession.RehydrateAsync(
                store, gitObjects, t, time, compatRestoreMsnFromSummary, pruneOpsBelowSummary))).Value;

    /// <summary>Allocation-free lookup: null when no live session exists.</summary>
    public DocumentSession? TryGet(string topic) =>
        _sessions.TryGetValue(topic, out var lazy) && lazy.IsValueCreated &&
        lazy.Value is { IsCompletedSuccessfully: true } task
            ? task.Result
            : null;

    /// <summary>Drop an idle session (Phase 8's eviction sweep).</summary>
    public bool TryEvict(string topic) => _sessions.TryRemove(topic, out _);

    public IReadOnlyList<string> LiveTopics() => [.. _sessions.Keys];
}
