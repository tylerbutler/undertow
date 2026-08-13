namespace Undertow.Protocol

/// Port of floodgate/auth: Authorization-header verification for the REST and
/// socket surfaces. Every rejection maps to HTTP 401 (the Routerlicious
/// contract, ADR-009) with levee-compatible messages.
module Auth =

    open Undertow.Protocol.Signet

    type AuthError =
        /// No Authorization header at all, as distinct from a malformed one.
        | MissingAuthorization
        | BadFormat
        | BadSignature
        | BadClaims of JwtValidationError

    let errorMessage error =
        match error with
        | MissingAuthorization -> "Missing Authorization header"
        | BadFormat -> "Invalid Authorization header format. Expected: Bearer <token>"
        | BadSignature -> "Invalid token signature"
        | BadClaims e -> Signet.formatError e

    let private mapCrypto e =
        match e with
        | Signet.BadFormat -> BadFormat
        | Signet.BadSignature -> BadSignature

    /// Verify HS256 token + validate connection claims against the topic ids
    /// (the socket connect_document path).
    let verify (token: string) (secret: string) (tenant: string) (doc: string) (now: int64) =
        verifySignature token secret
        |> Result.mapError mapCrypto
        |> Result.bind (fun claims ->
            validateConnectionClaims claims tenant doc now
            |> Result.mapError BadClaims
            |> Result.map (fun () -> claims))

    let private authorizationClaims (authorization: string) (secret: string) =
        extractToken authorization
        |> Result.mapError mapCrypto
        |> Result.bind (verifySignature >> (|>) secret >> Result.mapError mapCrypto)

    let private verifyWith validate (authorization: string) (secret: string) =
        authorizationClaims authorization secret
        |> Result.bind (fun claims ->
            validate claims |> Result.mapError BadClaims |> Result.map (fun () -> claims))

    let verifyWriteAuthorization authorization secret tenant doc now =
        verifyWith (fun c -> validateWriteAccess c tenant doc now) authorization secret

    let verifyReadAuthorization authorization secret tenant doc now =
        verifyWith (fun c -> validateReadAccess c tenant doc now) authorization secret

    /// Write access for a route with no document id in its path
    /// (POST /documents/:tenant): validate against the token's own document id.
    let verifyTenantWriteAuthorization authorization secret tenant now =
        authorizationClaims authorization secret
        |> Result.bind (fun claims ->
            validateWriteAccess claims tenant claims.DocumentId now
            |> Result.mapError BadClaims
            |> Result.map (fun () -> claims))

    let verifyStorageReadAuthorization authorization secret tenant now =
        authorizationClaims authorization secret
        |> Result.bind (fun claims ->
            validateReadAccess claims tenant claims.DocumentId now
            |> Result.mapError BadClaims
            |> Result.map (fun () -> claims))

    let verifyStorageWriteAuthorization authorization secret tenant now =
        authorizationClaims authorization secret
        |> Result.bind (fun claims ->
            validateSummaryAccess claims tenant claims.DocumentId now
            |> Result.mapError BadClaims
            |> Result.map (fun () -> claims))

    /// Verify the separate bearer credential for the token-mint endpoint:
    /// a fixed-time comparison against the configured secret.
    let verifyTokenMintAuthorization (authorization: string) (expectedSecret: string) : bool =
        match authorization.Split(' ') with
        | [| "Bearer"; token |] when token <> "" && expectedSecret <> "" ->
            let a = System.Text.Encoding.UTF8.GetBytes token
            let b = System.Text.Encoding.UTF8.GetBytes expectedSecret
            System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b)
        | _ -> false
