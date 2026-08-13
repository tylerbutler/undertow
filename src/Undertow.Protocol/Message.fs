namespace Undertow.Protocol

/// Port of spillway/message: protocol message types for the socket surface.
module Message =

    open Spillway

    /// IConnect — sent by client to initiate document collaboration.
    type ConnectMessage =
        {
            TenantId: string
            DocumentId: string
            Token: string option
            Client: Client
            Versions: string list
            DriverVersion: string option
            Mode: ConnectionMode
            Nonce: string option
            Epoch: string option
            SupportedFeatures: Map<string, Json> option
            RelayUserAgent: string option
        }

    /// Summary checkpoint metadata in a connect_document_success response.
    type SummaryContext =
        {
            Handle: string
            SequenceNumber: int64
        }

    /// Signal message (v2 format).
    type SignalMessage =
        {
            ClientId: string option
            Content: Json
            SignalType: string option
            ClientConnectionNumber: int64 option
            ReferenceSequenceNumber: int64 option
            TargetClientId: string option
        }

    /// IConnected — sent by server when connection succeeds.
    type ConnectedMessage =
        {
            Claims: TokenClaims
            ClientId: string
            Existing: bool
            MaxMessageSize: int64
            Mode: ConnectionMode
            ServiceConfiguration: ServiceConfiguration
            InitialClients: SignalClient list
            InitialMessages: SequencedDocumentMessage list
            InitialSignals: SignalMessage list
            SupportedVersions: string list
            SupportedFeatures: Map<string, Json>
            Version: string
            Timestamp: int64 option
            CheckpointSequenceNumber: int64 option
            Epoch: string option
            RelayServiceAgent: string option
            SummaryContext: SummaryContext option
        }

    /// Connection error response.
    type ConnectError = { Code: int; Message: string }

    /// Sent signal message (client -> server, v2).
    type SentSignalMessage =
        {
            Content: Json
            SignalType: string option
            ClientConnectionNumber: int64 option
            ReferenceSequenceNumber: int64 option
            TargetClientId: string option
        }

    /// Op broadcast message (server -> clients).
    type OpMessage =
        {
            DocumentId: string
            Ops: SequencedDocumentMessage list
        }

    type MessageType =
        | NoOp
        | ClientJoin
        | ClientLeave
        | Propose
        | Reject
        | Accept
        | Summarize
        | SummaryAck
        | SummaryNack
        | Operation
        | NoClient
        | RoundTrip
        | Control

    let messageTypeToString mt =
        match mt with
        | NoOp -> "noop"
        | ClientJoin -> "join"
        | ClientLeave -> "leave"
        | Propose -> "propose"
        | Reject -> "reject"
        | Accept -> "accept"
        | Summarize -> "summarize"
        | SummaryAck -> "summaryAck"
        | SummaryNack -> "summaryNack"
        | Operation -> "op"
        | NoClient -> "noClient"
        | RoundTrip -> "tripComplete"
        | Control -> "control"

    let messageTypeFromString s =
        match s with
        | "noop" -> Some NoOp
        | "join" -> Some ClientJoin
        | "leave" -> Some ClientLeave
        | "propose" -> Some Propose
        | "reject" -> Some Reject
        | "accept" -> Some Accept
        | "summarize" -> Some Summarize
        | "summaryAck" -> Some SummaryAck
        | "summaryNack" -> Some SummaryNack
        | "op" -> Some Operation
        | "noClient" -> Some NoClient
        | "tripComplete" -> Some RoundTrip
        | "control" -> Some Control
        | _ -> None
