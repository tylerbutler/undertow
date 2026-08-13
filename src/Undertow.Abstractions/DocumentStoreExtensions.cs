namespace Undertow.Abstractions;

public static class DocumentStoreExtensions
{
    /// <summary>
    /// The checkpoint is a rebuildable cache: when no row exists, reproduce
    /// Gleam's <c>from_checkpoint(max(maxOpSn, summarySn), summarySn)</c> from
    /// the ops and summary tables, at version 0.
    /// </summary>
    public static async ValueTask<CheckpointRecord> LoadOrSynthesizeCheckpointAsync(
        this IDocumentStore store, string topic, CancellationToken ct = default)
    {
        var checkpoint = await store.LoadCheckpointAsync(topic, ct);
        if (checkpoint is not null)
            return checkpoint;

        var maxOpSn = await store.GetMaxOpSequenceNumberAsync(topic, ct) ?? 0;
        var summarySn = (await store.GetSummaryAsync(topic, ct))?.SequenceNumber ?? 0;
        return new CheckpointRecord(
            SequenceNumber: Math.Max(maxOpSn, summarySn),
            MinimumSequenceNumber: summarySn,
            Version: 0,
            UpdatedAt: 0);
    }

    /// <summary>
    /// The `existing` flag every join-shaped reply carries: a three-way OR of
    /// document row, any stored op, and a non-empty summary handle. It drives
    /// container load-vs-create directly — do not simplify.
    /// </summary>
    public static async ValueTask<bool> StoredDocumentExistsAsync(
        this IDocumentStore store, string topic, CancellationToken ct = default)
    {
        if (await store.HasDocumentAsync(topic, ct))
            return true;
        if (await store.GetMaxOpSequenceNumberAsync(topic, ct) is not null)
            return true;
        var summary = await store.GetSummaryAsync(topic, ct);
        return summary is { Handle: not "" };
    }
}
