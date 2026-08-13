using Undertow.Abstractions;
using Undertow.Protocol;

namespace Undertow.Runtime;

public sealed record ConnectResult(
    bool Existing,
    KeyValuePair<string, string>[] Roster,
    KeyValuePair<long, string>[] InitialOps,
    string SummaryHandle,
    long SummarySequenceNumber,
    long CurrentSequenceNumber,
    long MembershipSn,
    string? MembershipMessage);

public sealed record SubmitResult(bool Assigned, long Sn, long Msn, string? Message, long CurrentSn);

public sealed record LeaveResult(long Sn, long Msn, string Message);

public sealed record SummaryMessagesResult(
    bool Assigned, long SummarySn, long ResponseSn, long Msn,
    string? SummaryMessage, string? ResponseMessage, long CurrentSn);

public enum CreateInitializedOutcome
{
    Created,
    AlreadyExists,
    InvalidInitialSummary,
}

/// <summary>
/// Per-document session: sequence state, capped op history, summary pointer,
/// and presence roster behind one SemaphoreSlim(1,1). The critical section
/// runs, in order: pure sequencing → pure message build → one storage
/// transaction (ops + checkpoint under the etag) → non-blocking broadcast
/// enqueue → in-memory commit. The enqueue stays inside the lock so two
/// concurrent ops can never interleave on a socket out of order.
/// </summary>
public sealed class DocumentSession
{
    /// <summary>Ops retained for initialMessages (levee's @max_history_size).</summary>
    public const int MaxHistorySize = 1000;

    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly IDocumentStore _store;
    private readonly IGitObjectStore _gitObjects;
    private readonly string _topic;
    private readonly TimeProvider _time;

    // All mutable state below is guarded by _sem.
    private Sequencing.SequenceState _seq;
    private List<KeyValuePair<long, string>> _history; // newest first, capped
    private (string Handle, long Sn) _summary;
    private readonly Dictionary<string, string> _presence = [];
    private long _version;
    private long _lastTouchedMs;
    private readonly bool _pruneOpsBelowSummary;

    private DocumentSession(
        IDocumentStore store, IGitObjectStore gitObjects, string topic, TimeProvider time,
        Sequencing.SequenceState seq, List<KeyValuePair<long, string>> history,
        (string, long) summary, long version, bool pruneOpsBelowSummary)
    {
        _pruneOpsBelowSummary = pruneOpsBelowSummary;
        _store = store;
        _gitObjects = gitObjects;
        _topic = topic;
        _time = time;
        _seq = seq;
        _history = history;
        _summary = summary;
        _version = version;
        _lastTouchedMs = time.GetTimestamp();
    }

    public string Topic => _topic;

    /// <summary>Monotonic ms of the last mutation, for the idle sweep.</summary>
    public long LastTouchedMs => Interlocked.Read(ref _lastTouchedMs);

    private void Touch() => Interlocked.Exchange(ref _lastTouchedMs, _time.GetTimestamp());

    /// <summary>
    /// Rehydrate a session from storage — a pure function of storage + topic,
    /// which is what makes sessions disposable: losing one costs the roster,
    /// never the sequence numbering. Reproduces Gleam's
    /// from_checkpoint(max(maxOpSn, summarySn), summarySn) when no checkpoint
    /// row exists; a stored row restores the live MSN unless the compat flag
    /// asks for the summary-SN restore Gleam does.
    /// </summary>
    public static async Task<DocumentSession> RehydrateAsync(
        IDocumentStore store, IGitObjectStore gitObjects, string topic, TimeProvider time,
        bool compatRestoreMsnFromSummary, bool pruneOpsBelowSummary = false,
        CancellationToken ct = default)
    {
        var summary = await store.GetSummaryAsync(topic, ct);
        var summaryHandle = summary?.Handle ?? "";
        var summarySn = summary?.SequenceNumber ?? 0;
        var checkpoint = await store.LoadCheckpointAsync(topic, ct);

        long sn, msn, version;
        if (checkpoint is null)
        {
            var maxOpSn = await store.GetMaxOpSequenceNumberAsync(topic, ct) ?? 0;
            sn = Math.Max(maxOpSn, summarySn);
            msn = summarySn;
            version = 0;
        }
        else
        {
            sn = checkpoint.SequenceNumber;
            msn = compatRestoreMsnFromSummary ? summarySn : checkpoint.MinimumSequenceNumber;
            version = checkpoint.Version;
        }

        // Repair the one crash prefix that is not benign: a summary pointer
        // with no ref, which makes the document unloadable. Idempotent; never
        // overwrites an existing ref.
        if (summaryHandle.Length > 0 && topic.Split(':') is ["document", var tenant, var documentId] &&
            await gitObjects.GetRefAsync(tenant, $"refs/heads/{documentId}", ct) is null)
        {
            await gitObjects.PutRefAsync(tenant, $"refs/heads/{documentId}", summaryHandle, ct);
        }

        var recent = await store.GetOpsAsync(topic, Math.Max(0, sn - MaxHistorySize), null, ct);
        var history = recent
            .Select(op => new KeyValuePair<long, string>(op.SequenceNumber, op.Payload))
            .Reverse()
            .Take(MaxHistorySize)
            .ToList();

        return new DocumentSession(
            store, gitObjects, topic, time,
            Sequencing.fromCheckpoint(sn, msn), history, (summaryHandle, summarySn), version,
            pruneOpsBelowSummary);
    }

    // ── Storage commit under the etag ───────────────────────────────────────

    private async ValueTask CommitAsync(OpRecord[] ops, long sn, long msn, CancellationToken ct)
    {
        var next = new CheckpointRecord(sn, msn, _version + 1, _time.GetUtcNow().ToUnixTimeMilliseconds());
        if (!await _store.CommitSequencedAsync(_topic, ops, next, _version, ct))
        {
            // Single-node: no other writer exists, so this indicates external
            // interference with the database rather than a normal race.
            throw new InvalidOperationException($"checkpoint version conflict on {_topic}");
        }

        _version = next.Version;
    }

    private void Remember(long sn, string message)
    {
        _history.Insert(0, new KeyValuePair<long, string>(sn, message));
        if (_history.Count > MaxHistorySize)
            _history.RemoveAt(_history.Count - 1);
    }

    private async ValueTask<bool> AlreadyExistsAsync(CancellationToken ct) =>
        // A hydrated session that has seen any mutation counts as existing via
        // storage; unlike Gleam there is no not-yet-persisted cached doc state.
        await _store.StoredDocumentExistsAsync(_topic, ct);

    // ── Session operations (each one critical section) ──────────────────────

    /// <summary>
    /// connect: registers presence, and in write mode assigns + persists the
    /// sequenced join op, invoking <paramref name="broadcast"/> (op payload
    /// enqueue — must not block) inside the lock.
    /// </summary>
    public async ValueTask<ConnectResult> ConnectAsync(
        string clientId, string mode, string clientJson, string joinData, long timestampMs,
        Action<long, string>? broadcast = null, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            var existing = await AlreadyExistsAsync(ct);
            await _store.CreateDocumentAsync(_topic, _time.GetUtcNow().ToUnixTimeSeconds(), ct);
            var roster = _presence.ToArray();
            var (handle, summarySn) = _summary;

            if (mode == "write")
            {
                var sn = _seq.SequenceNumber + 1;
                var joined = Sequencing.clientJoin(_seq, clientId, _seq.SequenceNumber);
                var seq = new Sequencing.SequenceState(sn, joined.MinimumSequenceNumber, joined.ClientStates);
                var message = DocumentProtocol.systemMessage("join", joinData, sn, seq.MinimumSequenceNumber, timestampMs);

                await CommitAsync([new OpRecord(sn, message)], sn, seq.MinimumSequenceNumber, ct);
                broadcast?.Invoke(sn, message);

                _seq = seq;
                Remember(sn, message);
                _presence[clientId] = clientJson;
                Touch();

                var initialOps = _history.AsEnumerable().Reverse().ToArray();
                return new ConnectResult(existing, roster, initialOps, handle, summarySn, sn, sn, message);
            }
            else
            {
                _presence[clientId] = clientJson;
                Touch();
                var initialOps = _history.AsEnumerable().Reverse().ToArray();
                return new ConnectResult(
                    existing, roster, initialOps, handle, summarySn, _seq.SequenceNumber, -1, null);
            }
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>submitOp: assign, build the wire message with the assigned
    /// SN/MSN, persist, enqueue, commit.</summary>
    public async ValueTask<SubmitResult> SubmitMessageAsync(
        string clientId, long csn, long rsn, Func<long, long, string> build,
        Action<long, string>? broadcast = null, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            switch (Sequencing.assignSequenceNumber(_seq, clientId, csn, rsn))
            {
                case Sequencing.SequenceResult.SequenceOk ok:
                    var message = build(ok.assignedSn, ok.msn);
                    await CommitAsync([new OpRecord(ok.assignedSn, message)], ok.assignedSn, ok.msn, ct);
                    broadcast?.Invoke(ok.assignedSn, message);
                    _seq = ok.state;
                    Remember(ok.assignedSn, message);
                    Touch();
                    return new SubmitResult(true, ok.assignedSn, ok.msn, message, ok.assignedSn);
                default:
                    return new SubmitResult(false, 0, 0, null, _seq.SequenceNumber);
            }
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>
    /// A summarize op and its server response (ack or nack) assigned and stored
    /// together. `build(summarySn, responseSn, msn)` returns (summarizeMessage,
    /// responseMessage, summaryHandle-or-null); a non-null handle advances the
    /// summary pointer in the same storage transaction.
    /// </summary>
    public async ValueTask<SummaryMessagesResult> SubmitSummaryMessagesAsync(
        string clientId, long csn, long rsn,
        Func<long, long, long, (string SummaryMessage, string ResponseMessage, string? Handle)> build,
        Action<long, string, long, string>? broadcast = null, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            switch (Sequencing.assignSequenceNumber(_seq, clientId, csn, rsn))
            {
                case Sequencing.SequenceResult.SequenceOk ok:
                    var summarySn = ok.assignedSn;
                    var responseSn = summarySn + 1;
                    var seq = new Sequencing.SequenceState(
                        responseSn, ok.state.MinimumSequenceNumber, ok.state.ClientStates);
                    var (summaryMessage, responseMessage, handle) = build(summarySn, responseSn, ok.msn);

                    // One transaction: both ops + checkpoint; the summary
                    // pointer write follows inside the same lock. Every crash
                    // prefix is safe: ops without a pointer replay cleanly.
                    await CommitAsync(
                        [new OpRecord(summarySn, summaryMessage), new OpRecord(responseSn, responseMessage)],
                        responseSn, ok.msn, ct);
                    if (handle is not null)
                    {
                        await _store.PutSummaryAsync(_topic, new SummaryRecord(handle, summarySn), ct);

                        // Post-parity, opt-in: ops below the last summary are
                        // only reachable through requestOps / GET /deltas, so
                        // pruning them changes those results — default off.
                        if (_pruneOpsBelowSummary)
                            await _store.PruneOpsBelowAsync(_topic, summarySn, ct);
                    }

                    broadcast?.Invoke(summarySn, summaryMessage, responseSn, responseMessage);

                    _seq = seq;
                    Remember(summarySn, summaryMessage);
                    Remember(responseSn, responseMessage);
                    if (handle is not null)
                        _summary = (handle, summarySn);
                    Touch();
                    return new SummaryMessagesResult(
                        true, summarySn, responseSn, ok.msn, summaryMessage, responseMessage, responseSn);
                default:
                    return new SummaryMessagesResult(false, 0, 0, 0, null, null, _seq.SequenceNumber);
            }
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>Sequenced leave for a write-mode client.</summary>
    public async ValueTask<LeaveResult> LeaveSequencedAsync(
        string clientId, long timestampMs, Action<long, string>? broadcast = null, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            var sn = _seq.SequenceNumber + 1;
            var left = Sequencing.clientLeave(_seq, clientId);
            var seq = new Sequencing.SequenceState(sn, left.MinimumSequenceNumber, left.ClientStates);
            var data = DocumentProtocol.leaveData(clientId);
            var message = DocumentProtocol.systemMessage("leave", data, sn, seq.MinimumSequenceNumber, timestampMs);

            await CommitAsync([new OpRecord(sn, message)], sn, seq.MinimumSequenceNumber, ct);
            broadcast?.Invoke(sn, message);

            _seq = seq;
            Remember(sn, message);
            _presence.Remove(clientId);
            Touch();
            return new LeaveResult(sn, seq.MinimumSequenceNumber, message);
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>Presence-only departure for a read-mode client.</summary>
    public async ValueTask LeavePresenceAsync(string clientId, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            _presence.Remove(clientId);
            Touch();
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>RSN advance from a noop; RSN can only increase.</summary>
    public async ValueTask UpdateClientRsnAsync(string clientId, long rsn, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            var updated = Sequencing.updateClientRsn(_seq, clientId, rsn);
            if (updated.IsOk)
            {
                _seq = updated.ResultValue;
                // Persist the advanced MSN so a restart cannot regress it.
                await CommitAsync([], _seq.SequenceNumber, _seq.MinimumSequenceNumber, ct);
                Touch();
            }
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>Create with an optional initial summary (the REST create path).</summary>
    public async ValueTask<CreateInitializedOutcome> CreateInitializedAsync(
        string tenant, string body, long nowSeconds, CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            if (await AlreadyExistsAsync(ct))
                return CreateInitializedOutcome.AlreadyExists;

            var plan = InitialSummaryBoundary.plan(body, nowSeconds, sha =>
                _gitObjects.GetObjectAsync(tenant, sha, ct).AsTask().GetAwaiter().GetResult() is not null);

            switch (plan.Status)
            {
                case InitialSummaryStatus.Invalid:
                    return CreateInitializedOutcome.InvalidInitialSummary;

                case InitialSummaryStatus.NoSummary:
                    await _store.CreateDocumentAsync(_topic, nowSeconds, ct);
                    _seq = Sequencing.create();
                    _summary = ("", 0);
                    _history = [];
                    Touch();
                    return CreateInitializedOutcome.Created;

                default:
                    foreach (var (sha, objectBody) in plan.Objects)
                        await _gitObjects.PutObjectAsync(tenant, sha, objectBody, ct);
                    await _store.CreateDocumentAsync(_topic, nowSeconds, ct);
                    await _store.PutSummaryAsync(
                        _topic, new SummaryRecord(plan.CommitSha, plan.SequenceNumber), ct);
                    _seq = Sequencing.fromCheckpoint(plan.SequenceNumber, plan.SequenceNumber);
                    _summary = (plan.CommitSha, plan.SequenceNumber);
                    _history = [];
                    Touch();
                    return CreateInitializedOutcome.Created;
            }
        }
        finally
        {
            _sem.Release();
        }
    }

    // ── Read accessors (still serialized for a consistent view) ─────────────

    public async ValueTask<string[]> ClientsAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            return [.. _presence.Keys];
        }
        finally
        {
            _sem.Release();
        }
    }

    public async ValueTask<KeyValuePair<string, string>[]> RosterAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            return [.. _presence];
        }
        finally
        {
            _sem.Release();
        }
    }

    public async ValueTask<long> SequenceNumberAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            return _seq.SequenceNumber;
        }
        finally
        {
            _sem.Release();
        }
    }

    public async ValueTask<(string Handle, long Sn)> SummaryAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            return _summary;
        }
        finally
        {
            _sem.Release();
        }
    }

    /// <summary>Whether the session has connected clients (for idle eviction).</summary>
    public async ValueTask<int> PresenceCountAsync(CancellationToken ct = default)
    {
        await _sem.WaitAsync(ct);
        try
        {
            return _presence.Count;
        }
        finally
        {
            _sem.Release();
        }
    }
}
