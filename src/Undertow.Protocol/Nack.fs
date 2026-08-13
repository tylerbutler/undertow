namespace Undertow.Protocol

/// Port of spillway/nack. Message strings are read by clients and tests —
/// keep them byte-identical to the Gleam originals.
module Nack =

    open Spillway

    type NackErrorType =
        | ThrottlingError
        | InvalidScopeError
        | BadRequestError
        | LimitExceededError

    let nackErrorTypeToString t =
        match t with
        | ThrottlingError -> "ThrottlingError"
        | InvalidScopeError -> "InvalidScopeError"
        | BadRequestError -> "BadRequestError"
        | LimitExceededError -> "LimitExceededError"

    let nackErrorTypeFromString s =
        match s with
        | "ThrottlingError" -> Some ThrottlingError
        | "InvalidScopeError" -> Some InvalidScopeError
        | "BadRequestError" -> Some BadRequestError
        | "LimitExceededError" -> Some LimitExceededError
        | _ -> None

    type NackContent =
        {
            Code: int
            ErrorType: NackErrorType
            Message: string
            RetryAfter: int64 option
        }

    type Nack =
        {
            Operation: DocumentMessage option
            SequenceNumber: int64
            Content: NackContent
        }

    let private make code errorType message retryAfter op =
        {
            Operation = op
            SequenceNumber = -1L
            Content =
                {
                    Code = code
                    ErrorType = errorType
                    Message = message
                    RetryAfter = retryAfter
                }
        }

    let badRequest message op =
        make 400 BadRequestError message None op

    let invalidScope requiredScope op =
        make 403 InvalidScopeError $"Missing required scope: %s{requiredScope}" None op

    let throttled (retryAfterSeconds: int64) op =
        make 429 ThrottlingError "Rate limit exceeded" (Some retryAfterSeconds) op

    let limitExceeded message op =
        make 429 LimitExceededError message None op

    let readOnlyClient op =
        make 400 BadRequestError "Client is in read-only mode" None op

    let invalidCsn (expected: int64) (received: int64) op =
        make
            400
            BadRequestError
            $"Invalid client sequence number: expected > %d{expected}, received %d{received}"
            None
            op

    let messageTooLarge (maxSize: int64) (actualSize: int64) op =
        make 413 BadRequestError $"Message size %d{actualSize} exceeds limit %d{maxSize}" None op

    let invalidRsn (currentSn: int64) (receivedRsn: int64) op =
        make
            400
            BadRequestError
            $"Invalid RSN: current SN is %d{currentSn}, received %d{receivedRsn}"
            None
            op

    let unknownClient (clientId: string) =
        make 400 BadRequestError $"Unknown client: %s{clientId}" None None
