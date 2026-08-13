namespace Undertow.Protocol

/// Port of spillway/session_logic: pure decision logic for document sessions.
module SessionLogic =

    /// Negotiate features between server and client capabilities.
    ///
    /// - Server true, client true -> true
    /// - Server true, client unspecified -> true (advertise)
    /// - Server true, client false -> false
    /// - Otherwise -> server value
    let negotiateFeatures (serverFeatures: Map<string, bool>) (clientFeatures: Map<string, bool>) =
        serverFeatures
        |> Map.map (fun feature serverValue ->
            match serverValue, Map.tryFind feature clientFeatures with
            | true, Some true -> true
            | true, None -> true
            | true, Some false -> false
            | _ -> serverValue)

    /// Negotiate protocol version: first server version present in the client's
    /// list, mapped from range to concrete; fallback "0.1.0".
    let negotiateVersion (supportedVersions: string list) (clientVersions: string list) =
        match supportedVersions |> List.tryFind (fun sv -> List.contains sv clientVersions) with
        | Some "^0.1.0" -> "0.1.0"
        | Some "^1.0.0" -> "1.0.0"
        | Some v -> v
        | None -> "0.1.0"

    /// Validate that summarize contents carry all required fields.
    let validateSummarizeContents (contents: Map<string, Json>) : Result<unit, string> =
        let required = [ "handle"; "message"; "parents"; "head" ]

        let missing =
            required |> List.filter (fun field -> not (Map.containsKey field contents))

        match missing with
        | [] -> Ok()
        | _ -> Error("missing fields: " + String.concat ", " missing)

    /// Determine which clients receive a signal.
    ///
    /// Priority: targetedClients > ignoredClients > single target > broadcast.
    /// Targeted lists are intersected with the known clients, and the sender
    /// never receives its own signal.
    let determineSignalRecipients
        (senderClientId: string)
        (targetedClients: string list option)
        (ignoredClients: string list option)
        (singleTarget: string option)
        (allClientIds: string list)
        : string list =
        match targetedClients, ignoredClients, singleTarget with
        | Some targets, _, _ ->
            targets
            |> List.filter (fun c -> c <> senderClientId)
            |> List.filter (fun c -> List.contains c allClientIds)
        | None, Some ignored, _ ->
            allClientIds
            |> List.filter (fun c -> c <> senderClientId && not (List.contains c ignored))
        | None, None, Some target ->
            if target <> senderClientId && List.contains target allClientIds then
                [ target ]
            else
                []
        | None, None, None -> allClientIds |> List.filter (fun c -> c <> senderClientId)

    /// Add an op to the history (newest first) and trim to max size.
    let addToHistory (op: 'a) (history: 'a list) (maxSize: int) =
        op :: history |> List.truncate maxSize

    type SequencedOpParams =
        {
            ClientId: string
            SequenceNumber: int64
            MinimumSequenceNumber: int64
            ClientSequenceNumber: int64
            ReferenceSequenceNumber: int64
            OpType: string
            Contents: Json
            Metadata: Json
            Timestamp: int64
        }

    /// Build a sequenced op as an ordered field list (the wire order).
    let buildSequencedOp (p: SequencedOpParams) : (string * Json) list =
        [
            "clientId", JStr p.ClientId
            "sequenceNumber", JInt p.SequenceNumber
            "minimumSequenceNumber", JInt p.MinimumSequenceNumber
            "clientSequenceNumber", JInt p.ClientSequenceNumber
            "referenceSequenceNumber", JInt p.ReferenceSequenceNumber
            "type", JStr p.OpType
            "contents", p.Contents
            "metadata", p.Metadata
            "timestamp", JInt p.Timestamp
        ]

    /// Build a summaryAck as an ordered field list. The ack consumes SN sn+1
    /// and references the summarize op's SN.
    let buildSummaryAck
        (handle: string)
        (sn: int64)
        (msn: int64)
        (timestamp: int64)
        : (string * Json) list =
        let contents =
            JObj
                [
                    "handle", JStr handle
                    "summaryProposal", JObj [ "summarySequenceNumber", JInt sn ]
                ]

        [
            "clientId", JNull
            "sequenceNumber", JInt(sn + 1L)
            "minimumSequenceNumber", JInt msn
            "clientSequenceNumber", JInt -1L
            "referenceSequenceNumber", JInt sn
            "type", JStr "summaryAck"
            "contents", contents
            "metadata", JNull
            "timestamp", JInt timestamp
        ]
