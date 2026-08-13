namespace Undertow.Protocol

/// Port of the pure half of floodgate/document_channel: payload parsing and
/// wire-shape building for the document channel. No IO, no session state.
module DocumentProtocol =

    open System.Text.Json
    open Undertow.Protocol.Signet

    // ── Submitted ops ───────────────────────────────────────────────────────

    /// A submitted op with optional fields kept as raw JSON text ("" = absent).
    /// Contents defaults to null (the JSON literal) like the Gleam decoder.
    type SubmittedOp =
        {
            ClientSequenceNumber: int64
            ReferenceSequenceNumber: int64
            Kind: string
            ContentsJson: string
            MetadataJson: string
            ServerMetadataJson: string
            TracesJson: string
            CompressionJson: string
        }

    let private rawOrEmpty (el: JsonElement) (name: string) : string =
        match Dyn.tryField name el with
        | Some v when v.ValueKind <> JsonValueKind.Null -> v.GetRawText()
        | Some v -> v.GetRawText() // explicit null is still present
        | None -> ""

    let private decodeSubmittedOp (el: JsonElement) : SubmittedOp option =
        match Dyn.intField "clientSequenceNumber" el, Dyn.intField "referenceSequenceNumber" el with
        | Some csn, Some rsn ->
            Some
                {
                    ClientSequenceNumber = csn
                    ReferenceSequenceNumber = rsn
                    Kind = Dyn.stringField "type" el |> Option.defaultValue "op"
                    ContentsJson =
                        match Dyn.tryField "contents" el with
                        | Some v -> v.GetRawText()
                        | None -> "null"
                    MetadataJson = rawOrEmpty el "metadata"
                    ServerMetadataJson = rawOrEmpty el "serverMetadata"
                    TracesJson = rawOrEmpty el "traces"
                    CompressionJson = rawOrEmpty el "compression"
                }
        | _ -> None

    /// Parse `{messageBatches: [[op, ...], ...]}` into a flat op list.
    /// null = malformed payload.
    let parseSubmittedOps (payload: JsonElement) : SubmittedOp[] | null =
        match Dyn.tryField "messageBatches" payload |> Option.bind Dyn.tryArray with
        | None -> null
        | Some batches ->
            let ops =
                batches
                |> List.map (fun batch ->
                    match Dyn.tryArray batch with
                    | None -> None
                    | Some entries ->
                        let decoded = entries |> List.map decodeSubmittedOp

                        if List.forall Option.isSome decoded then
                            Some(List.choose id decoded)
                        else
                            None)

            if List.forall Option.isSome ops then
                ops |> List.choose id |> List.concat |> Array.ofList
            else
                null

    // ── JSON canonicalization (dynamic_json equivalent) ─────────────────────

    /// Parse raw JSON text and re-render with objects key-sorted, mirroring the
    /// Erlang map round-trip every dynamic payload goes through. Invalid or
    /// absent input renders as null, matching dynamic_json's fallback.
    let private canonicalJson (raw: string) : Json =
        if raw = "" then
            JNull
        else
            match Dyn.tryParseString raw with
            | Some doc ->
                use doc = doc
                Json.canonicalize (Dyn.toJson doc.RootElement)
            | None -> JNull

    /// The same round-trip as a public string -> string (assert-0x4b2 path).
    let normalizeClientJson (clientJson: string) : string =
        Json.toString (canonicalJson clientJson)

    // ── Client payloads ─────────────────────────────────────────────────────

    let userName (claims: TokenClaims) : string =
        match Map.tryFind "name" claims.User.Properties with
        | Some(JStr name) -> name
        | _ -> claims.User.Id

    /// The server-built IClient used when the peer sent none. Already
    /// normalized (key-sorted), matching client_json in the Gleam original.
    let serverClientJson (mode: string) (claims: TokenClaims) : string =
        JObj
            [
                "mode", JStr mode
                "details", JObj [ "capabilities", JObj [ "interactive", JBool true ] ]
                "permission", JArr []
                "scopes", JArr(scopesToStrings claims.Scopes |> List.map JStr)
                "user", JObj [ "id", JStr claims.User.Id; "name", JStr(userName claims) ]
            ]
        |> Json.canonicalize
        |> Json.toString

    /// The IClient the peer supplied in its connect payload, re-rendered
    /// compactly with its ORIGINAL key order preserved; null when absent or
    /// not an object (fall back to the server-built record).
    ///
    /// Deliberate divergence from Gleam floodgate, which key-sorts here
    /// (normalize_client_json) because Erlang maps cannot preserve order. The
    /// container-loader seeds its own audience entry with the object it sent
    /// (as-constructed key order) and asserts byte-identity (0x4b2) against
    /// every later echo of that client — so sorting trips the assert and
    /// closes the container in live multi-user flows (reproduced against
    /// Gleam floodgate; Elixir levee echoes verbatim and passes). Preserving
    /// the supplied order keeps every server-side copy — join op detail,
    /// initialClients, presence signals — byte-identical to each other AND to
    /// the client's own seed, which is the identity 0x4b2 actually demands.
    let suppliedClientJson (payload: JsonElement) : string | null =
        match Dyn.tryField "client" payload with
        | Some client when client.ValueKind = JsonValueKind.Object ->
            Json.toString (Dyn.toJson client)
        | _ -> null

    /// The join op's `data`: a JSON *string* of {clientId, detail}. The client
    /// payload is already canonicalized before stringification, so the
    /// join-op-vs-initialClients identity holds.
    let clientJoinData (clientId: string) (clientJson: string) : string =
        JObj
            [
                "clientId", JStr clientId
                "detail", JRaw(System.ReadOnlyMemory(System.Text.Encoding.UTF8.GetBytes clientJson))
            ]
        |> Json.toString

    // ── Presence signals ────────────────────────────────────────────────────

    let presenceJoinPayload (clientId: string) (clientJson: string) : byte[] =
        let content =
            JObj
                [
                    "type", JStr "join"
                    "content",
                    JObj
                        [
                            "clientId", JStr clientId
                            "client",
                            JRaw(
                                System.ReadOnlyMemory(System.Text.Encoding.UTF8.GetBytes clientJson)
                            )
                        ]
                ]

        Json.toUtf8 (JObj [ "clientId", JNull; "content", JStr(Json.toString content) ])

    let presenceLeavePayload (clientId: string) : byte[] =
        let content = JObj [ "type", JStr "leave"; "content", JStr clientId ]
        Json.toUtf8 (JObj [ "clientId", JNull; "content", JStr(Json.toString content) ])

    // ── System messages and stored-message rendering ────────────────────────

    /// A sequenced system message (join/leave): clientId null, csn/rsn -1,
    /// contents null, `data` a JSON string.
    let systemMessage
        (kind: string)
        (data: string)
        (sn: int64)
        (msn: int64)
        (timestamp: int64)
        : string =
        JObj
            [
                "clientId", JNull
                "sequenceNumber", JInt sn
                "minimumSequenceNumber", JInt msn
                "clientSequenceNumber", JInt -1L
                "referenceSequenceNumber", JInt -1L
                "type", JStr kind
                "contents", JNull
                "data", JStr data
                "timestamp", JInt timestamp
            ]
        |> Json.toString

    /// The leave op's `data`: the departed client id as a JSON string literal.
    let leaveData (clientId: string) : string = Json.toString (JStr clientId)

    let private storedMessageJson (sn: int64, stored: string) : Json =
        let hasSequenceNumber =
            match Dyn.tryParseString stored with
            | Some doc ->
                use doc = doc

                doc.RootElement.ValueKind = JsonValueKind.Object
                && (Dyn.intField "sequenceNumber" doc.RootElement |> Option.isSome)
            | None -> false

        if hasSequenceNumber then
            JRaw(System.ReadOnlyMemory(System.Text.Encoding.UTF8.GetBytes stored))
        else
            JObj [ "sequenceNumber", JInt sn; "contents", JStr stored ]

    /// `[storedMessage, ...]` — the `op` event payload.
    let opsArrayJson (ops: System.Collections.Generic.KeyValuePair<int64, string>[]) : byte[] =
        Json.toUtf8 (
            JArr(
                ops
                |> Array.map (fun kv -> storedMessageJson (kv.Key, kv.Value))
                |> List.ofArray
            )
        )

    // ── Sequenced ops and nacks ─────────────────────────────────────────────

    let private optionalRaw (fields: (string * Json) list) (name: string) (raw: string) =
        if raw = "" then
            fields
        else
            fields @ [ name, canonicalJson raw ]

    /// The sequenced-op wire shape for a client-submitted op.
    let sequencedOpJson
        (clientId: string)
        (op: SubmittedOp)
        (sn: int64)
        (msn: int64)
        (timestamp: int64)
        : string =
        let fields =
            [
                "clientId", JStr clientId
                "sequenceNumber", JInt sn
                "minimumSequenceNumber", JInt msn
                "clientSequenceNumber", JInt op.ClientSequenceNumber
                "referenceSequenceNumber", JInt op.ReferenceSequenceNumber
                "type", JStr op.Kind
                "contents", canonicalJson op.ContentsJson
                "timestamp", JInt timestamp
            ]

        let fields = optionalRaw fields "metadata" op.MetadataJson
        let fields = optionalRaw fields "serverMetadata" op.ServerMetadataJson
        let fields = optionalRaw fields "traces" op.TracesJson
        let fields = optionalRaw fields "compression" op.CompressionJson
        Json.toString (JObj fields)

    /// One nack entry. `op` null for nacks that carry no operation.
    let private nackJson (op: SubmittedOp | null) (sn: int64) (code: int) (message: string) : Json =
        let operationJson =
            match op with
            | null -> JNull
            | op ->
                let fields =
                    [
                        "clientSequenceNumber", JInt op.ClientSequenceNumber
                        "referenceSequenceNumber", JInt op.ReferenceSequenceNumber
                        "type", JStr op.Kind
                        "contents", canonicalJson op.ContentsJson
                    ]

                let fields = optionalRaw fields "metadata" op.MetadataJson
                let fields = optionalRaw fields "serverMetadata" op.ServerMetadataJson
                let fields = optionalRaw fields "traces" op.TracesJson
                let fields = optionalRaw fields "compression" op.CompressionJson
                JObj fields

        JObj
            [
                "operation", operationJson
                "sequenceNumber", JInt sn
                "content",
                JObj
                    [
                        "code", JInt(int64 code)
                        "type", JStr "BadRequestError"
                        "message", JStr message
                    ]
            ]

    /// `[nack]` — the nack event payload for a single rejection.
    let nackArrayJson (op: SubmittedOp | null) (sn: int64) (code: int) (message: string) : byte[] =
        Json.toUtf8 (JArr [ nackJson op sn code message ])

    /// A nack payload carrying several rejections in submission order.
    let nackListJson (nacks: (SubmittedOp | null * int64 * int * string)[]) : byte[] =
        Json.toUtf8 (
            JArr(
                nacks
                |> Array.map (fun (op, sn, code, message) -> nackJson op sn code message)
                |> List.ofArray
            )
        )

    // ── Summary wire shapes ─────────────────────────────────────────────────

    let summaryAckJson
        (handle: string)
        (summarySn: int64)
        (msn: int64)
        (timestamp: int64)
        : string =
        SessionLogic.buildSummaryAck handle summarySn msn timestamp
        |> JObj
        |> Json.toString

    let summaryNackJson
        (summarySn: int64)
        (responseSn: int64)
        (msn: int64)
        (reason: string)
        (timestamp: int64)
        : string =
        JObj
            [
                "clientId", JNull
                "sequenceNumber", JInt responseSn
                "minimumSequenceNumber", JInt msn
                "clientSequenceNumber", JInt -1L
                "referenceSequenceNumber", JInt summarySn
                "type", JStr "summaryNack"
                "contents",
                JObj
                    [
                        "summaryProposal", JObj [ "summarySequenceNumber", JInt summarySn ]
                        "code", JInt 400L
                        "message", JStr reason
                    ]
                "metadata", JNull
                "timestamp", JInt timestamp
            ]
        |> Json.toString

    /// Summarize-op contents, validated field-presence-first so the error
    /// message lists what is missing.
    type SummarizeParse =
        {
            Ok: bool
            Error: string
            Handle: string
            Message: string
            Parents: string[]
            Head: string
        }

    let parseSummarizeContents (contentsJson: string) : SummarizeParse =
        let fail error =
            {
                Ok = false
                Error = error
                Handle = ""
                Message = ""
                Parents = [||]
                Head = ""
            }

        match Dyn.tryParseString contentsJson with
        | None -> fail "Summary contents must be an object"
        | Some doc ->
            use doc = doc
            let el = doc.RootElement

            if el.ValueKind <> JsonValueKind.Object then
                fail "Summary contents must be an object"
            else
                let fields =
                    el.EnumerateObject()
                    |> Seq.map (fun p -> p.Name, Dyn.toJson p.Value)
                    |> Map.ofSeq

                match SessionLogic.validateSummarizeContents fields with
                | Error reason -> fail ("Invalid summarize op: " + reason)
                | Ok() ->
                    let parents =
                        Dyn.tryField "parents" el
                        |> Option.bind Dyn.tryArray
                        |> Option.map (
                            List.choose (fun p ->
                                if p.ValueKind = JsonValueKind.String then
                                    Some(nonNull (p.GetString()))
                                else
                                    None)
                        )

                    match
                        Dyn.stringField "handle" el,
                        Dyn.stringField "message" el,
                        parents,
                        Dyn.stringField "head" el
                    with
                    | Some handle, Some message, Some parents, Some head ->
                        {
                            Ok = true
                            Error = ""
                            Handle = handle
                            Message = message
                            Parents = Array.ofList parents
                            Head = head
                        }
                    | _ -> fail "Summary contents have invalid field types"

    /// The summary commit object body for a summarize op.
    let summaryCommitBody
        (handle: string)
        (parents: string[])
        (message: string)
        (nowSeconds: int64)
        : string =
        let author =
            JObj
                [
                    "name", JStr "Floodgate"
                    "email", JStr "server@floodgate.local"
                    "date", JStr(string nowSeconds)
                ]

        JObj
            [
                "tree", JStr handle
                "parents", JArr(parents |> Array.map JStr |> List.ofArray)
                "message", JStr message
                "author", author
                "committer", author
            ]
        |> Json.toString

    // ── The connected response (IConnected) ─────────────────────────────────

    let claimsJson (claims: TokenClaims) : Json =
        JObj
            [
                "documentId", JStr claims.DocumentId
                "scopes", JArr(scopesToStrings claims.Scopes |> List.map JStr)
                "tenantId", JStr claims.TenantId
                "user", JObj [ "id", JStr claims.User.Id ]
                "iat", JInt claims.IssuedAt
                "exp", JInt claims.Expiration
                "ver", JStr claims.Version
            ]

    /// connect_document_success. Roster entries are (clientId, storedClientJson);
    /// initial ops are (sn, storedMessageJson); initial signals are rendered
    /// payload bytes.
    let connectedResponse
        (claims: TokenClaims)
        (clientId: string)
        (mode: string)
        (existing: bool)
        (roster: System.Collections.Generic.KeyValuePair<string, string>[])
        (initialOps: System.Collections.Generic.KeyValuePair<int64, string>[])
        (initialSignals: byte[][])
        (summaryHandle: string)
        (summarySequenceNumber: int64)
        (currentSequenceNumber: int64)
        (maxMessageSize: int64)
        : byte[] =
        let raw (bytes: byte[]) = JRaw(System.ReadOnlyMemory bytes)

        let rawString (s: string) =
            raw (System.Text.Encoding.UTF8.GetBytes s)

        JObj
            [
                "claims", claimsJson claims
                "clientId", JStr clientId
                "existing", JBool existing
                "maxMessageSize", JInt maxMessageSize
                "mode", JStr mode
                "serviceConfiguration",
                JObj [ "blockSize", JInt 65536L; "maxMessageSize", JInt maxMessageSize ]
                "initialClients",
                JArr(
                    roster
                    |> Array.map (fun kv ->
                        JObj [ "clientId", JStr kv.Key; "client", rawString kv.Value ])
                    |> List.ofArray
                )
                "initialMessages",
                JArr(
                    initialOps
                    |> Array.map (fun kv -> storedMessageJson (kv.Key, kv.Value))
                    |> List.ofArray
                )
                "initialSignals", JArr(initialSignals |> Array.map raw |> List.ofArray)
                "supportedVersions", JArr [ JStr "^0.1.0"; JStr "^1.0.0" ]
                "version", JStr "1.0.0"
                "checkpointSequenceNumber", JInt currentSequenceNumber
                "summaryHandle", JStr summaryHandle
                "summarySequenceNumber", JInt summarySequenceNumber
            ]
        |> Json.toUtf8

    // ── Connect authorization (socket path) ─────────────────────────────────

    /// Outcome of the topic/token gate. Claims is null when not Ok.
    type ConnectAuth =
        {
            Ok: bool
            Reason: string
            Code: int
            Message: string
            Claims: TokenClaims
            Scopes: string[]
        }

    let private connectFail reason code message : ConnectAuth =
        {
            Ok = false
            Reason = reason
            Code = code
            Message = message
            Claims = Unchecked.defaultof<TokenClaims>
            Scopes = [||]
        }

    /// Verify the topic names a document under this tenant and the token grants
    /// read access to it. Shared by both join paths.
    let authorizeTopicToken
        (configuredTenant: string)
        (secret: string)
        (topic: string)
        (token: string)
        (now: int64)
        : ConnectAuth =
        match topic.Split(':') with
        | [| "document"; tenant; doc |] when tenant = configuredTenant ->
            match Auth.verify token secret tenant doc now with
            | Error _ -> connectFail "unauthorized" 401 "Invalid or expired token"
            | Ok claims ->
                if List.contains DocRead claims.Scopes then
                    {
                        Ok = true
                        Reason = ""
                        Code = 0
                        Message = ""
                        Claims = claims
                        Scopes = Array.ofList (scopesToStrings claims.Scopes)
                    }
                else
                    connectFail "unauthorized" 403 "Token lacks document read scope"
        | _ -> connectFail "invalid_topic" 400 "Topic does not name a document in this tenant"

    /// Requested "write" mode needs doc:write in the token scopes.
    let modeScopeOk (mode: string) (scopes: string[]) : bool =
        mode <> "write" || Array.contains "doc:write" scopes

    /// The connection mode a payload requests; anything but "write" is read.
    let connectionMode (payload: JsonElement) : string =
        match Dyn.stringField "mode" payload with
        | Some "write" -> "write"
        | _ -> "read"

    // ── Submitted signals ───────────────────────────────────────────────────

    /// A normalized inbound signal for the C# fan-out. ContentJson is the
    /// canonically-rendered content; empty targeting arrays mean "absent".
    type ParsedSignal =
        {
            ContentJson: string
            Targeted: bool
            TargetClientId: string
            HasTargetClientId: bool
            TargetedClients: string[]
            HasTargetedClients: bool
            IgnoredClients: string[]
            HasIgnoredClients: bool
        }

    let private toParsed (s: Signals.NormalizedSignal) : ParsedSignal =
        {
            ContentJson = Json.toString (Json.canonicalize s.Content)
            Targeted =
                Option.isSome s.TargetedClients
                || Option.isSome s.IgnoredClients
                || Option.isSome s.TargetClientId
            TargetClientId = s.TargetClientId |> Option.defaultValue ""
            HasTargetClientId = Option.isSome s.TargetClientId
            TargetedClients =
                s.TargetedClients |> Option.map Array.ofList |> Option.defaultValue [||]
            HasTargetedClients = Option.isSome s.TargetedClients
            IgnoredClients = s.IgnoredClients |> Option.map Array.ofList |> Option.defaultValue [||]
            HasIgnoredClients = Option.isSome s.IgnoredClients
        }

    /// Parse a submitSignal payload. `{contentBatches: [...]}` goes through
    /// spillway's v1/v2 normalization keeping targeting; the legacy
    /// `{signals: [{content}, ...] | [[str]]}` shape is untargeted strings.
    /// null = neither shape parsed.
    let parseSubmittedSignals (payload: JsonElement) : ParsedSignal[] | null =
        match Dyn.tryField "contentBatches" payload |> Option.bind Dyn.tryArray with
        | Some batches ->
            batches
            |> List.collect (fun batch -> Signals.normalizeSignalBatch (Dyn.toJson batch))
            |> List.map toParsed
            |> Array.ofList
        | None ->
            match Dyn.tryField "signals" payload |> Option.bind Dyn.tryArray with
            | None -> null
            | Some entries ->
                let asContentObject (e: JsonElement) =
                    if e.ValueKind = JsonValueKind.Object then
                        Dyn.stringField "content" e
                    else
                        None

                let asStringList (e: JsonElement) =
                    Dyn.tryArray e
                    |> Option.map (
                        List.choose (fun s ->
                            if s.ValueKind = JsonValueKind.String then
                                Some(nonNull (s.GetString()))
                            else
                                None)
                    )

                let contentObjects = entries |> List.map asContentObject

                let contents =
                    if List.forall Option.isSome contentObjects then
                        Some(List.choose id contentObjects)
                    else
                        let lists = entries |> List.map asStringList

                        let allComplete =
                            List.forall Option.isSome lists
                            && List.forall2
                                (fun (l: string list option) (e: JsonElement) ->
                                    match l, Dyn.tryArray e with
                                    | Some strings, Some raw ->
                                        List.length strings = List.length raw
                                    | _ -> false)
                                lists
                                entries

                        if allComplete then
                            Some(lists |> List.choose id |> List.concat)
                        else
                            None

                match contents with
                | None -> null
                | Some contents ->
                    contents
                    |> List.map (fun c -> toParsed (Signals.untargeted (JStr c)))
                    |> Array.ofList

    /// The `signal` event payload for one relayed signal.
    let signalMessagePayload (clientId: string) (contentJson: string) : byte[] =
        Json.toUtf8 (
            JObj
                [
                    "clientId", JStr clientId
                    "content",
                    JRaw(System.ReadOnlyMemory(System.Text.Encoding.UTF8.GetBytes contentJson))
                ]
        )

    /// Recipient resolution for a targeted signal — the SessionLogic variant,
    /// which intersects targets with the known clients (levee's behaviour).
    let signalRecipients
        (senderClientId: string)
        (signal: ParsedSignal)
        (allClientIds: string[])
        : string[] =
        SessionLogic.determineSignalRecipients
            senderClientId
            (if signal.HasTargetedClients then
                 Some(List.ofArray signal.TargetedClients)
             else
                 None)
            (if signal.HasIgnoredClients then
                 Some(List.ofArray signal.IgnoredClients)
             else
                 None)
            (if signal.HasTargetClientId then
                 Some signal.TargetClientId
             else
                 None)
            (List.ofArray allClientIds)
        |> Array.ofList
