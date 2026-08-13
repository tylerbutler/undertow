namespace Undertow.Protocol

/// Port of spillway/signals: v1/v2 signal normalization and targeting.
///
/// Signals are ephemeral — not sequenced, not persisted — so a wrong field
/// here produces no error and no failing test, just presence that quietly
/// doesn't work. Every branch mirrors the Gleam original.
module Signals =

    open Spillway

    // ── System signals ──────────────────────────────────────────────────────

    type SystemSignalType =
        | ClientJoinSignal
        | ClientLeaveSignal

    type ClientJoinContent = { ClientId: string; Client: Client }
    type ClientLeaveContent = { ClientId: string }

    type SystemSignal =
        | JoinSignal of ClientJoinContent
        | LeaveSignal of ClientLeaveContent

    let clientJoinSignal clientId client =
        JoinSignal { ClientId = clientId; Client = client }

    let clientLeaveSignal clientId = LeaveSignal { ClientId = clientId }

    // ── V1 (legacy) format ──────────────────────────────────────────────────

    type SignalAddress =
        | BroadcastAddress
        | ContainerAddress of string

    type SignalV1Contents = { SignalType: string; Content: Json }

    type SignalV1Envelope =
        {
            Address: string
            Contents: SignalV1Contents
            ClientBroadcastSignalSequenceNumber: int64
        }

    type SignalParseError =
        | InvalidFormat of string
        | MissingField of string

    // ── Decode helpers over the Json AST (Gleam's Dynamic analogue) ────────

    let private asString json =
        match json with
        | JStr s -> Some s
        | _ -> None

    let private asInt json =
        match json with
        | JInt i -> Some i
        | _ -> None

    let private asStringList json =
        match json with
        | JArr items ->
            let strings = items |> List.choose asString

            if List.length strings = List.length items then
                Some strings
            else
                None
        | _ -> None

    let private asMap json : Map<string, Json> =
        match json with
        | JObj fields -> Map.ofList fields
        | _ -> Map.empty

    /// Parse a v1 signal envelope from a map.
    let parseV1EnvelopeFromMap
        (raw: Map<string, Json>)
        : Result<SignalV1Envelope, SignalParseError> =
        match Map.tryFind "address" raw, Map.tryFind "contents" raw with
        | Some addr, Some contents ->
            let contentsMap = asMap contents

            Ok
                {
                    Address = asString addr |> Option.defaultValue ""
                    Contents =
                        {
                            SignalType =
                                Map.tryFind "type" contentsMap
                                |> Option.bind asString
                                |> Option.defaultValue ""
                            Content = Map.tryFind "content" contentsMap |> Option.defaultValue JNull
                        }
                    ClientBroadcastSignalSequenceNumber =
                        Map.tryFind "clientBroadcastSignalSequenceNumber" raw
                        |> Option.bind asInt
                        |> Option.defaultValue 0L
                }
        | _ -> Error(MissingField "address or contents")

    // ── Normalization (v1/v2 → internal format) ─────────────────────────────

    type NormalizedSignal =
        {
            Content: Json
            SignalType: string option
            ClientConnectionNumber: int64 option
            ReferenceSequenceNumber: int64 option
            TargetClientId: string option
            TargetedClients: string list option
            IgnoredClients: string list option
        }

    let private normalizeV1 (raw: Map<string, Json>) : NormalizedSignal =
        let contents =
            Map.tryFind "contents" raw |> Option.map asMap |> Option.defaultValue Map.empty

        {
            Content = Map.tryFind "content" contents |> Option.defaultValue JNull
            SignalType = Map.tryFind "type" contents |> Option.bind asString
            ClientConnectionNumber =
                Map.tryFind "clientBroadcastSignalSequenceNumber" raw |> Option.bind asInt
            ReferenceSequenceNumber = None
            TargetClientId = None
            TargetedClients = None
            IgnoredClients = None
        }

    let private normalizeV2 (raw: Map<string, Json>) : NormalizedSignal =
        // A wrapper envelope carries the signal under "signal"; an empty inner
        // map falls back to the envelope itself, as in the Gleam original.
        let inner =
            match Map.tryFind "signal" raw with
            | Some s ->
                let m = asMap s
                if Map.isEmpty m then raw else m
            | None -> raw

        {
            Content = Map.tryFind "content" inner |> Option.defaultValue JNull
            SignalType = Map.tryFind "type" inner |> Option.bind asString
            ClientConnectionNumber = Map.tryFind "clientConnectionNumber" inner |> Option.bind asInt
            ReferenceSequenceNumber =
                Map.tryFind "referenceSequenceNumber" inner |> Option.bind asInt
            TargetClientId = Map.tryFind "targetClientId" inner |> Option.bind asString
            // Targeting lives at the envelope level, not the inner signal.
            TargetedClients = Map.tryFind "targetedClients" raw |> Option.bind asStringList
            IgnoredClients = Map.tryFind "ignoredClients" raw |> Option.bind asStringList
        }

    /// Normalize a raw signal map. v1 is detected by "address"/"contents".
    let normalizeSignal (raw: Map<string, Json>) : NormalizedSignal =
        if Map.containsKey "address" raw || Map.containsKey "contents" raw then
            normalizeV1 raw
        else
            normalizeV2 raw

    /// Normalize a batch: a list of maps, or a single map; anything else is [].
    let normalizeSignalBatch (batch: Json) : NormalizedSignal list =
        match batch with
        | JArr items ->
            let maps =
                items
                |> List.choose (fun i ->
                    match i with
                    | JObj fields -> Some(Map.ofList fields)
                    | _ -> None)

            if List.length maps = List.length items then
                maps |> List.map normalizeSignal
            else
                []
        | JObj fields -> [ normalizeSignal (Map.ofList fields) ]
        | _ -> []

    /// An untargeted normalized signal wrapping raw content (the legacy path).
    let untargeted (content: Json) : NormalizedSignal =
        {
            Content = content
            SignalType = None
            ClientConnectionNumber = None
            ReferenceSequenceNumber = None
            TargetClientId = None
            TargetedClients = None
            IgnoredClients = None
        }

    // ── V2 format ───────────────────────────────────────────────────────────

    type SignalV2 =
        {
            Content: Json
            SignalType: string option
            ClientConnectionNumber: int64 option
            ReferenceSequenceNumber: int64 option
            TargetClientId: string option
        }

    type ClientBroadcastSignalEnvelope =
        {
            Signal: SignalV2
            TargetedClients: string list option
            IgnoredClients: string list option
        }

    // ── Targeting logic ─────────────────────────────────────────────────────

    let isTargeted (signal: SignalV2) = Option.isSome signal.TargetClientId

    let shouldReceive (signal: SignalV2) (clientId: string) =
        match signal.TargetClientId with
        | None -> true
        | Some target -> target = clientId

    /// Recipients for a v2 envelope. NOTE: unlike
    /// SessionLogic.determineSignalRecipients this does NOT intersect targets
    /// with the known clients — the channel layer must use the SessionLogic
    /// version (levee's behaviour is the reference).
    let getSignalRecipients
        (envelope: ClientBroadcastSignalEnvelope)
        (allClients: string list)
        (senderClientId: string)
        =
        match envelope.TargetedClients, envelope.IgnoredClients with
        | Some targets, _ -> targets |> List.filter (fun c -> c <> senderClientId)
        | None, Some ignored ->
            allClients
            |> List.filter (fun c -> c <> senderClientId && not (List.contains c ignored))
        | None, None -> allClients |> List.filter (fun c -> c <> senderClientId)

    let shouldClientReceiveSignal
        (envelope: ClientBroadcastSignalEnvelope)
        (clientId: string)
        (senderClientId: string)
        =
        if clientId = senderClientId then
            false
        else
            match envelope.TargetedClients, envelope.IgnoredClients with
            | Some targets, _ -> List.contains clientId targets
            | None, Some ignored -> not (List.contains clientId ignored)
            | None, None -> true

    // ── Constructors ────────────────────────────────────────────────────────

    let broadcast content signalType connectionNumber rsn : SignalV2 =
        {
            Content = content
            SignalType = signalType
            ClientConnectionNumber = connectionNumber
            ReferenceSequenceNumber = rsn
            TargetClientId = None
        }

    let targeted content targetClientId signalType connectionNumber rsn : SignalV2 =
        {
            Content = content
            SignalType = signalType
            ClientConnectionNumber = connectionNumber
            ReferenceSequenceNumber = rsn
            TargetClientId = Some targetClientId
        }

    let broadcastEnvelope signal : ClientBroadcastSignalEnvelope =
        {
            Signal = signal
            TargetedClients = None
            IgnoredClients = None
        }

    let targetedEnvelope signal targets : ClientBroadcastSignalEnvelope =
        {
            Signal = signal
            TargetedClients = Some targets
            IgnoredClients = None
        }

    let ignoredEnvelope signal ignored : ClientBroadcastSignalEnvelope =
        {
            Signal = signal
            TargetedClients = None
            IgnoredClients = Some ignored
        }

    // ── Server -> client signal message ─────────────────────────────────────

    let signalMessageFromV2 (senderClientId: string) (signal: SignalV2) : Message.SignalMessage =
        {
            ClientId = Some senderClientId
            Content = signal.Content
            SignalType = signal.SignalType
            ClientConnectionNumber = signal.ClientConnectionNumber
            ReferenceSequenceNumber = signal.ReferenceSequenceNumber
            TargetClientId = signal.TargetClientId
        }

    let systemSignalMessage (content: Json) (signalType: string) : Message.SignalMessage =
        {
            ClientId = None
            Content = content
            SignalType = Some signalType
            ClientConnectionNumber = None
            ReferenceSequenceNumber = None
            TargetClientId = None
        }

    // ── Version detection ───────────────────────────────────────────────────

    type SignalVersion =
        | V1Format
        | V2Format
        | UnknownFormat

    let detectSignalVersion hasAddress hasTargetedClients hasIgnoredClients =
        match hasAddress, hasTargetedClients || hasIgnoredClients with
        | true, false -> V1Format
        | _, true -> V2Format
        | false, false -> V2Format
