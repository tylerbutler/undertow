namespace Undertow.Protocol

/// Port of spillway/types: core Fluid protocol types. `Dynamic` fields become
/// the ordered `Json` AST so stored payloads round-trip byte-exactly.
module Spillway =

    type User = Signet.User
    type TokenClaims = Signet.TokenClaims
    type Scope = Signet.Scope

    /// Connection mode — determines if client can submit operations.
    type ConnectionMode =
        | WriteMode
        | ReadMode

    module ConnectionMode =
        let toString mode =
            match mode with
            | WriteMode -> "write"
            | ReadMode -> "read"

        let fromString value =
            match value with
            | "write" -> Some WriteMode
            | "read" -> Some ReadMode
            | _ -> None

    type ClientCapabilities = { Interactive: bool }

    type ClientDetails =
        {
            Capabilities: ClientCapabilities
            ClientType: string option
            Environment: string option
            Device: string option
        }

    type Client =
        {
            Mode: ConnectionMode
            Details: ClientDetails
            Permission: string list
            User: User
            Scopes: string list
            Timestamp: int64 option
        }

    /// Client with sequence information (for quorum tracking).
    type SequencedClient =
        {
            Client: Client
            SequenceNumber: int64
        }

    /// Client info sent with signals.
    type SignalClient =
        {
            ClientId: string
            Client: Client
            ClientConnectionNumber: int64 option
            ReferenceSequenceNumber: int64 option
        }

    type ServiceConfiguration =
        {
            BlockSize: int64
            MaxMessageSize: int64
            NoopTimeFrequency: int64 option
            NoopCountFrequency: int64 option
        }

    /// Latency trace point.
    type Trace =
        {
            Service: string
            Action: string
            Timestamp: int64
        }

    /// Document message (client -> server).
    type DocumentMessage =
        {
            ClientSequenceNumber: int64
            ReferenceSequenceNumber: int64
            MessageType: string
            Contents: Json
            Metadata: Json option
            ServerMetadata: Json option
            Traces: Trace list option
            Compression: string option
        }

    /// Branch origin for forked documents.
    type MessageOrigin =
        {
            Id: string
            SequenceNumber: int64
            MinimumSequenceNumber: int64
        }

    /// Sequenced document message (server -> clients).
    type SequencedDocumentMessage =
        {
            ClientId: string option
            SequenceNumber: int64
            MinimumSequenceNumber: int64
            ClientSequenceNumber: int64
            ReferenceSequenceNumber: int64
            MessageType: string
            Contents: Json
            Metadata: Json option
            ServerMetadata: Json option
            Origin: MessageOrigin option
            Traces: Trace list option
            Timestamp: int64
            Data: string option
        }
