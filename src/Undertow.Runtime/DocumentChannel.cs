using System.Text.Json;
using Undertow.Abstractions;
using Undertow.Protocol;

namespace Undertow.Runtime;

public sealed record DocAssigns(string ClientId, string Mode, string Topic, string[] Scopes, bool Connected)
{
    public static DocAssigns Pending(string topic) => new("", "", topic, [], false);
}

/// <summary>
/// The Fluid document channel — C# orchestration over the pure F#
/// DocumentProtocol decisions. Port of floodgate/document_channel.
/// </summary>
public sealed class DocumentChannel(
    DocumentRegistry documents,
    IDocumentStore store,
    IGitObjectStore gitObjects,
    string configuredTenant,
    string jwtSecret,
    long maxFrameBytes,
    TimeProvider time) : IChannelHandler
{
    private long NowSeconds() => time.GetUtcNow().ToUnixTimeSeconds();
    private long NowMs() => NowSeconds() * 1000;

    private static string Field(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : "";

    private static long IntField(JsonElement payload, string name, long fallback) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt64(out var value)
            ? value
            : fallback;

    // ── Join ────────────────────────────────────────────────────────────────

    public async ValueTask<JoinOutcome> JoinAsync(ChannelContext context, JsonElement payload)
    {
        // A Socket.IO join is a whole connect_document payload (it always
        // carries tenantId + id); a Phoenix phx_join carries only the token.
        var isConnectPayload = Field(payload, "tenantId").Length > 0 && Field(payload, "id").Length > 0;
        if (isConnectPayload)
        {
            var (response, error, assigns) = await ConnectCoreAsync(context, payload, context.SocketId);
            return error is null
                ? new JoinOutcome(true, response!, HasReply: true, assigns)
                : new JoinOutcome(false, JoinErrorJson(error), HasReply: true, null);
        }

        var auth = DocumentProtocol.authorizeTopicToken(
            configuredTenant, jwtSecret, context.Topic, Field(payload, "token"), NowSeconds());
        return auth.Ok
            ? new JoinOutcome(true, ReadOnlyMemory<byte>.Empty, HasReply: false, DocAssigns.Pending(context.Topic))
            : new JoinOutcome(false, JoinErrorJson(new ConnectFailure(auth.Reason, auth.Code, auth.Message)), true, null);
    }

    private sealed record ConnectFailure(string Reason, int Code, string Message);

    private static byte[] JoinErrorJson(ConnectFailure error) =>
        System.Text.Encoding.UTF8.GetBytes(
            $$"""{"reason":{{JsonSerializerString(error.Reason)}}}""");

    private static byte[] ConnectErrorJson(ConnectFailure error) =>
        System.Text.Encoding.UTF8.GetBytes(
            $$"""{"code":{{error.Code}},"message":{{JsonSerializerString(error.Message)}}}""");

    private static string JsonSerializerString(string value) => JsonSerializer.Serialize(value);

    /// <summary>Authorize, open the session, fan out the join, and build the
    /// connected response. Shared by the Socket.IO join and the Phoenix
    /// connect_document.</summary>
    private async ValueTask<(byte[]? Response, ConnectFailure? Error, DocAssigns? Assigns)> ConnectCoreAsync(
        ChannelContext context, JsonElement payload, string clientId)
    {
        var auth = DocumentProtocol.authorizeTopicToken(
            configuredTenant, jwtSecret, context.Topic, Field(payload, "token"), NowSeconds());
        if (!auth.Ok)
            return (null, new ConnectFailure(auth.Reason, auth.Code, auth.Message), null);

        if (payload.ValueKind != JsonValueKind.Object)
            return (null, new ConnectFailure("unauthorized", 400, "Malformed connect_document payload"), null);

        var mode = DocumentProtocol.connectionMode(payload);
        if (!DocumentProtocol.modeScopeOk(mode, auth.Scopes))
            return (null, new ConnectFailure("unauthorized", 403, "Write mode requires document write scope"), null);

        // Echo the peer's own IClient when it sent one, so the audience sees a
        // single payload for this client id (assert 0x4b2).
        var client = DocumentProtocol.suppliedClientJson(payload)
                     ?? DocumentProtocol.serverClientJson(mode, auth.Claims);

        var session = await documents.GetOrCreateAsync(context.Topic);
        var presenceJoin = DocumentProtocol.presenceJoinPayload(clientId, client);

        var result = await session.ConnectAsync(
            clientId, mode, client, DocumentProtocol.clientJoinData(clientId, client), NowMs(),
            broadcast: (sn, message) =>
            {
                // The joining client receives its own join op in
                // initialMessages; excluding it from fan-out avoids an early
                // duplicate before the connect response lands.
                context.Broadcaster.BroadcastFrom(
                    clientId, context.Topic, FluidEvents.Op,
                    DocumentProtocol.opsArrayJson([new KeyValuePair<long, string>(sn, message)]));
            });

        if (mode != "write")
        {
            // Read-mode presence join goes out as a signal to peers.
            context.Broadcaster.BroadcastFrom(clientId, context.Topic, FluidEvents.Signal, presenceJoin);
        }

        // initialSignals is always empty, matching levee and (now) Gleam
        // floodgate: returning the client's own presence-join used to close
        // containers with assert 0x4b2 against payloads whose key order had
        // changed in transit. Undertow's verbatim echo made it harmless, but
        // the wire shape stays aligned across all three servers.
        var response = DocumentProtocol.connectedResponse(
            auth.Claims, clientId, mode, result.Existing, result.Roster, result.InitialOps,
            [],
            result.SummaryHandle, result.SummarySequenceNumber, result.CurrentSequenceNumber,
            maxFrameBytes);

        return (response, null, new DocAssigns(clientId, mode, context.Topic, auth.Scopes, true));
    }

    // ── handle_in ───────────────────────────────────────────────────────────

    public async ValueTask<HandleOutcome> HandleInAsync(ChannelContext context, string @event, JsonElement payload)
    {
        var assigns = context.Assigns as DocAssigns ?? DocAssigns.Pending(context.Topic);
        switch (@event, assigns.Connected)
        {
            case (FluidEvents.ConnectDocument, false):
                // Phoenix path only: IConnect arrives as an event after the
                // join, and the driver listens for a pushed result, not a reply.
                var (response, error, newAssigns) = await ConnectCoreAsync(context, payload, context.SocketId);
                if (error is null)
                {
                    context.Assigns = newAssigns;
                    return HandleOutcome.Push(FluidEvents.ConnectDocumentSuccess, response!);
                }

                return HandleOutcome.Push(FluidEvents.ConnectDocumentError, ConnectErrorJson(error));

            case (FluidEvents.SubmitOp, false):
                return HandleOutcome.Push(
                    FluidEvents.Nack, DocumentProtocol.nackArrayJson(null, 0, 400, "Client not connected"));

            case (_, false):
                return HandleOutcome.NoReply;

            case (FluidEvents.SubmitOp, true):
                return await SubmitOpAsync(context, payload, assigns);

            case (FluidEvents.SubmitSignal, true):
                return await SubmitSignalsAsync(context, payload, assigns);

            case ("requestOps", true):
                var from = IntField(payload, "from", 0);
                var ops = await store.GetOpsAsync(assigns.Topic, from, null);
                return HandleOutcome.Push(
                    FluidEvents.Op,
                    DocumentProtocol.opsArrayJson(
                        [.. ops.Select(o => new KeyValuePair<long, string>(o.SequenceNumber, o.Payload))]));

            case ("noop", true):
                // Without this an idle levee-mode client never advances its RSN
                // and the document's MSN stalls.
                if (Field(payload, "clientId") == assigns.ClientId)
                {
                    var session = await documents.GetOrCreateAsync(assigns.Topic);
                    await session.UpdateClientRsnAsync(
                        assigns.ClientId, IntField(payload, "referenceSequenceNumber", 0));
                }

                return HandleOutcome.NoReply;

            case (FluidEvents.SubmitSummary, true):
                return HandleOutcome.Push(
                    FluidEvents.Nack,
                    DocumentProtocol.nackArrayJson(
                        null, await SequenceNumberAsync(assigns.Topic), 400,
                        "Submit summaries as sequenced summarize operations"));

            default:
                return HandleOutcome.NoReply;
        }
    }

    private async ValueTask<long> SequenceNumberAsync(string topic)
    {
        if (documents.TryGet(topic) is { } live)
            return await live.SequenceNumberAsync();
        return (await store.LoadOrSynthesizeCheckpointAsync(topic)).SequenceNumber;
    }

    private async ValueTask<HandleOutcome> SubmitOpAsync(ChannelContext context, JsonElement payload, DocAssigns assigns)
    {
        if (assigns.Mode != "write")
        {
            return HandleOutcome.Push(
                FluidEvents.Nack,
                DocumentProtocol.nackArrayJson(null, 0, 403, "Read-only clients cannot submit operations"));
        }

        if (Field(payload, "clientId") != assigns.ClientId)
        {
            return HandleOutcome.Push(
                FluidEvents.Nack, DocumentProtocol.nackArrayJson(null, 0, 400, "Client ID mismatch"));
        }

        var ops = DocumentProtocol.parseSubmittedOps(payload);
        if (ops is null)
        {
            return HandleOutcome.Push(
                FluidEvents.Nack, DocumentProtocol.nackArrayJson(null, 0, 400, "Malformed submitOp payload"));
        }

        var session = await documents.GetOrCreateAsync(assigns.Topic);
        var nacks = new List<(DocumentProtocol.SubmittedOp?, long, int, string)>();

        foreach (var op in ops)
        {
            if (op.Kind == "summarize")
            {
                if (!assigns.Scopes.Contains("summary:write"))
                {
                    nacks.Add((op, await SequenceNumberAsync(assigns.Topic), 403, "Summary scope required"));
                    continue;
                }

                await SubmitSummaryOpAsync(context, session, op, assigns, nacks);
                continue;
            }

            var result = await session.SubmitMessageAsync(
                assigns.ClientId, op.ClientSequenceNumber, op.ReferenceSequenceNumber,
                build: (sn, msn) => DocumentProtocol.sequencedOpJson(assigns.ClientId, op, sn, msn, NowMs()),
                broadcast: (sn, message) => context.Broadcaster.Broadcast(
                    assigns.Topic, FluidEvents.Op,
                    DocumentProtocol.opsArrayJson([new KeyValuePair<long, string>(sn, message)])));

            if (!result.Assigned)
                nacks.Add((op, result.CurrentSn, 400, "Invalid client or reference sequence number"));
        }

        return nacks.Count == 0
            ? HandleOutcome.NoReply
            : HandleOutcome.Push(
                FluidEvents.Nack,
                DocumentProtocol.nackListJson([.. nacks.Select(n => Tuple.Create(n.Item1, n.Item2, n.Item3, n.Item4))]));
    }

    private async ValueTask SubmitSummaryOpAsync(
        ChannelContext context, DocumentSession session, DocumentProtocol.SubmittedOp op, DocAssigns assigns,
        List<(DocumentProtocol.SubmittedOp?, long, int, string)> nacks)
    {
        var tenant = assigns.Topic.Split(':') is ["document", var t, _] ? t : "";

        var result = await session.SubmitSummaryMessagesAsync(
            assigns.ClientId, op.ClientSequenceNumber, op.ReferenceSequenceNumber,
            build: (summarySn, responseSn, msn) =>
            {
                var parse = DocumentProtocol.parseSummarizeContents(op.ContentsJson);
                string? handle = null;
                string failure;
                if (!parse.Ok)
                {
                    failure = parse.Error;
                }
                else if (gitObjects.GetObjectAsync(tenant, parse.Handle).AsTask().GetAwaiter().GetResult() is null)
                {
                    failure = "Summary tree does not exist";
                }
                else
                {
                    var commitBody = DocumentProtocol.summaryCommitBody(
                        parse.Handle, parse.Parents, parse.Message, NowSeconds());
                    // The commit is content-addressed, so an orphan left by a
                    // crash is garbage rather than a wrong answer.
                    if (SiltBoundary.objectId("commits", commitBody) is { } sha)
                    {
                        gitObjects.PutObjectAsync(tenant, sha, commitBody).AsTask().GetAwaiter().GetResult();
                        handle = sha;
                        failure = "";
                    }
                    else
                    {
                        failure = "Could not store summary commit";
                    }
                }

                var responseMessage = handle is not null
                    ? DocumentProtocol.summaryAckJson(handle, summarySn, msn, NowMs())
                    : DocumentProtocol.summaryNackJson(summarySn, responseSn, msn, failure, NowMs());
                var summaryMessage = DocumentProtocol.sequencedOpJson(assigns.ClientId, op, summarySn, msn, NowMs());
                return (summaryMessage, responseMessage, handle);
            },
            broadcast: (summarySn, summaryMessage, responseSn, responseMessage) =>
                context.Broadcaster.Broadcast(
                    assigns.Topic, FluidEvents.Op,
                    DocumentProtocol.opsArrayJson(
                    [
                        new KeyValuePair<long, string>(summarySn, summaryMessage),
                        new KeyValuePair<long, string>(responseSn, responseMessage),
                    ])));

        if (!result.Assigned)
        {
            nacks.Add((op, result.CurrentSn, 400, "Invalid client or reference sequence number"));
            return;
        }

        // The session's summary pointer is committed; make the ref match it.
        // Reading the pointer back is what makes the ref a projection of the
        // authoritative value.
        var (handle, _) = await session.SummaryAsync();
        if (handle.Length > 0 && tenant.Length > 0 &&
            assigns.Topic.Split(':') is ["document", _, var documentId])
        {
            await gitObjects.PutRefAsync(tenant, $"refs/heads/{documentId}", handle);
        }
    }

    private async ValueTask<HandleOutcome> SubmitSignalsAsync(
        ChannelContext context, JsonElement payload, DocAssigns assigns)
    {
        if (Field(payload, "clientId") != assigns.ClientId)
            return HandleOutcome.NoReply;

        var signals = DocumentProtocol.parseSubmittedSignals(payload);
        if (signals is null)
            return HandleOutcome.NoReply;

        foreach (var signal in signals)
        {
            var message = DocumentProtocol.signalMessagePayload(assigns.ClientId, signal.ContentJson);
            if (!signal.Targeted)
            {
                // Untargeted keeps the broadcast path: one coordinator message
                // rather than one per recipient.
                context.Broadcaster.Broadcast(assigns.Topic, FluidEvents.Signal, message);
                continue;
            }

            var clients = documents.TryGet(assigns.Topic) is { } session
                ? await session.ClientsAsync()
                : [];
            foreach (var recipient in DocumentProtocol.signalRecipients(assigns.ClientId, signal, clients))
                context.Broadcaster.Push(recipient, assigns.Topic, FluidEvents.Signal, message);
        }

        return HandleOutcome.NoReply;
    }

    // ── terminate ───────────────────────────────────────────────────────────

    public async ValueTask TerminateAsync(ChannelContext context)
    {
        // A Phoenix socket that joined but never connected holds no session
        // membership; nothing to tear down or announce.
        if (context.Assigns is not DocAssigns { Connected: true } assigns)
            return;

        var session = await documents.GetOrCreateAsync(assigns.Topic);
        if (assigns.Mode == "write")
        {
            await session.LeaveSequencedAsync(
                assigns.ClientId, NowMs(),
                broadcast: (sn, message) => context.Broadcaster.Broadcast(
                    assigns.Topic, FluidEvents.Op,
                    DocumentProtocol.opsArrayJson([new KeyValuePair<long, string>(sn, message)])));
        }
        else
        {
            await session.LeavePresenceAsync(assigns.ClientId);
            context.Broadcaster.Broadcast(
                assigns.Topic, FluidEvents.Signal, DocumentProtocol.presenceLeavePayload(assigns.ClientId));
        }
    }
}
