namespace Undertow.Protocol

/// Port of spillway/validation: incoming-message validation.
module Validation =

    open Spillway

    type ValidationError =
        | MessageTooLarge of max: int64 * actual: int64
        | MissingField of name: string
        | InvalidField of name: string * reason: string
        | InvalidClientSequenceNumber of expectedGt: int64 * received: int64
        | InvalidReferenceSequenceNumber of currentSn: int64 * received: int64
        | TokenExpired of expiredAt: int64 * currentTime: int64
        | MissingScope of required: string * available: string list
        | OperationNotAllowed of mode: ConnectionMode * operation: string

    let validateMessageSize (messageBytes: int64) (maxSize: int64) =
        if messageBytes <= maxSize then
            Ok()
        else
            Error(MessageTooLarge(maxSize, messageBytes))

    let validateWriteMode mode =
        match mode with
        | WriteMode -> Ok()
        | ReadMode -> Error(OperationNotAllowed(ReadMode, "submitOp"))

    /// Wire-string scope check against the typed claims scopes.
    let validateScope (claims: TokenClaims) (requiredScope: string) =
        let available = Signet.scopesToStrings claims.Scopes

        match Signet.scopeFromString requiredScope with
        | Some scope when List.contains scope claims.Scopes -> Ok()
        | _ -> Error(MissingScope(requiredScope, available))

    let validateTokenExpiration (claims: TokenClaims) (currentTimeSeconds: int64) =
        if claims.Expiration > currentTimeSeconds then
            Ok()
        else
            Error(TokenExpired(claims.Expiration, currentTimeSeconds))

    let validateTokenClaims (claims: TokenClaims) (tenantId: string) (documentId: string) =
        if claims.TenantId <> tenantId then
            Error(InvalidField("tenantId", "Token tenant does not match request"))
        elif claims.DocumentId <> documentId then
            Error(InvalidField("documentId", "Token document does not match request"))
        else
            Ok()

    let validateCsn (receivedCsn: int64) (lastCsn: int64) =
        if receivedCsn > lastCsn then
            Ok()
        else
            Error(InvalidClientSequenceNumber(lastCsn, receivedCsn))

    let validateRsn (receivedRsn: int64) (currentSn: int64) =
        if receivedRsn <= currentSn then
            Ok()
        else
            Error(InvalidReferenceSequenceNumber(currentSn, receivedRsn))

    /// Full submit validation chain: write mode -> size -> CSN -> RSN.
    let validateDocumentMessage
        (msg: DocumentMessage)
        (clientMode: ConnectionMode)
        (lastCsn: int64)
        (currentSn: int64)
        (maxMessageSize: int64)
        (messageBytes: int64)
        =
        validateWriteMode clientMode
        |> Result.bind (fun () -> validateMessageSize messageBytes maxMessageSize)
        |> Result.bind (fun () -> validateCsn msg.ClientSequenceNumber lastCsn)
        |> Result.bind (fun () -> validateRsn msg.ReferenceSequenceNumber currentSn)

    let formatError error =
        match error with
        | MessageTooLarge(max, actual) -> $"Message size %d{actual} exceeds limit %d{max}"
        | MissingField name -> $"Missing required field: %s{name}"
        | InvalidField(name, reason) -> $"Invalid field '%s{name}': %s{reason}"
        | InvalidClientSequenceNumber(expectedGt, received) ->
            $"Invalid client sequence number: expected > %d{expectedGt}, received %d{received}"
        | InvalidReferenceSequenceNumber(currentSn, received) ->
            $"Invalid reference sequence number: current SN is %d{currentSn}, received RSN %d{received}"
        | TokenExpired(expiredAt, _) -> $"Token expired at %d{expiredAt}"
        | MissingScope(required, _) -> $"Missing required scope: %s{required}"
        | OperationNotAllowed(_, operation) ->
            $"Operation '%s{operation}' not allowed in read-only mode"
