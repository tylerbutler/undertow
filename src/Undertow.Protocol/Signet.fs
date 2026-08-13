namespace Undertow.Protocol

/// Port of signet: Fluid token types, claim validation, and HS256 JWT
/// verification/minting. Error messages are wire-adjacent (tests read them),
/// so they must match the Gleam originals byte for byte.
module Signet =

    open System
    open System.Security.Cryptography
    open System.Text

    // ── signet/types ────────────────────────────────────────────────────────

    type Scope =
        | DocRead
        | DocWrite
        | SummaryRead
        | SummaryWrite

    /// A Fluid user identity as carried in a token's `user` claim. Properties
    /// carry anything beyond `id` (signet keeps `name` here).
    type User =
        {
            Id: string
            Properties: Map<string, Json>
        }

    type TokenClaims =
        {
            DocumentId: string
            Scopes: Scope list
            TenantId: string
            User: User
            IssuedAt: int64
            Expiration: int64
            Version: string
            Jti: string option
        }

    let scopeToString scope =
        match scope with
        | DocRead -> "doc:read"
        | DocWrite -> "doc:write"
        | SummaryRead -> "summary:read"
        | SummaryWrite -> "summary:write"

    let scopeFromString value =
        match value with
        | "doc:read" -> Some DocRead
        | "doc:write" -> Some DocWrite
        | "summary:read" -> Some SummaryRead
        | "summary:write" -> Some SummaryWrite
        | _ -> None

    let scopesToStrings scopes = List.map scopeToString scopes

    /// Decode wire scope strings, dropping any unrecognized ones.
    let scopesFromStrings scopes = List.choose scopeFromString scopes

    // ── signet/jwt: claim validation ────────────────────────────────────────

    type JwtValidationError =
        | TokenExpired of expiredAt: int64 * currentTime: int64
        | TenantMismatch of tokenTenant: string * requestTenant: string
        | DocumentMismatch of tokenDocument: string * requestDocument: string
        | MissingScope of required: Scope * available: Scope list
        | MissingClaim of claimName: string
        | InvalidClaim of claimName: string * reason: string

    let validateExpiration (claims: TokenClaims) (currentTimeSeconds: int64) =
        if claims.Expiration > currentTimeSeconds then
            Ok()
        else
            Error(TokenExpired(claims.Expiration, currentTimeSeconds))

    let validateTenant (claims: TokenClaims) (requestTenantId: string) =
        if claims.TenantId = requestTenantId then
            Ok()
        else
            Error(TenantMismatch(claims.TenantId, requestTenantId))

    let validateDocument (claims: TokenClaims) (requestDocumentId: string) =
        if claims.DocumentId = requestDocumentId then
            Ok()
        else
            Error(DocumentMismatch(claims.DocumentId, requestDocumentId))

    let validateScope (claims: TokenClaims) (requiredScope: Scope) =
        if List.contains requiredScope claims.Scopes then
            Ok()
        else
            Error(MissingScope(requiredScope, claims.Scopes))

    let hasScope (claims: TokenClaims) scope = List.contains scope claims.Scopes
    let hasReadScope claims = hasScope claims DocRead
    let hasWriteScope claims = hasScope claims DocWrite
    let hasSummaryWriteScope claims = hasScope claims SummaryWrite

    /// Per spec section 3.3: expiration, then tenant, then document. The order
    /// is observable (the error message differs by which check fires first).
    let validateConnectionClaims claims tenantId documentId currentTimeSeconds =
        validateExpiration claims currentTimeSeconds
        |> Result.bind (fun () -> validateTenant claims tenantId)
        |> Result.bind (fun () -> validateDocument claims documentId)

    let validateReadAccess claims tenantId documentId currentTimeSeconds =
        validateConnectionClaims claims tenantId documentId currentTimeSeconds
        |> Result.bind (fun () -> validateScope claims DocRead)

    let validateWriteAccess claims tenantId documentId currentTimeSeconds =
        validateReadAccess claims tenantId documentId currentTimeSeconds
        |> Result.bind (fun () -> validateScope claims DocWrite)

    let validateSummaryAccess claims tenantId documentId currentTimeSeconds =
        validateReadAccess claims tenantId documentId currentTimeSeconds
        |> Result.bind (fun () -> validateScope claims SummaryWrite)

    let formatError error =
        match error with
        | TokenExpired(expiredAt, currentTime) ->
            $"Token expired at {expiredAt} (current time: {currentTime})"
        | TenantMismatch(tokenTenant, requestTenant) ->
            $"Token tenant '{tokenTenant}' does not match request tenant '{requestTenant}'"
        | DocumentMismatch(tokenDocument, requestDocument) ->
            $"Token document '{tokenDocument}' does not match request document '{requestDocument}'"
        | MissingScope(required, _) -> $"Missing required scope: {scopeToString required}"
        | MissingClaim claimName -> $"Missing required claim: {claimName}"
        | InvalidClaim(claimName, reason) -> $"Invalid claim '{claimName}': {reason}"

    let errorToHttpCode error =
        match error with
        | TokenExpired _ -> 401
        | TenantMismatch _ -> 403
        | DocumentMismatch _ -> 403
        | MissingScope _ -> 403
        | MissingClaim _ -> 401
        | InvalidClaim _ -> 401

    // ── signet/jwt: crypto + wire parsing ───────────────────────────────────

    type JwtCryptoError =
        | BadFormat
        | BadSignature

    let private b64UrlEncode (bytes: byte[]) =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private b64UrlDecode (text: string) : byte[] option =
        let padded =
            let t = text.Replace('-', '+').Replace('_', '/')

            match t.Length % 4 with
            | 2 -> t + "=="
            | 3 -> t + "="
            | 0 -> t
            | _ -> t + "===" // invalid; Convert will throw and we return None

        try
            Some(Convert.FromBase64String padded)
        with _ ->
            None

    let private b64Decode (text: string) : byte[] option =
        try
            Some(Convert.FromBase64String text)
        with _ ->
            None

    /// Extract a bare JWT from an `Authorization` header value: Routerlicious
    /// `Basic base64(user:jwt)`, plain `Basic <jwt>`, or `Bearer <jwt>`.
    let extractToken (authorization: string) : Result<string, JwtCryptoError> =
        match authorization.Split(' ') with
        | [| "Bearer"; token |] when token <> "" -> Ok token
        | [| "Basic"; token |] when token <> "" ->
            if token.Contains '.' then
                Ok token
            else
                match b64Decode token with
                | None -> Error BadFormat
                | Some bytes ->
                    let credentials = Encoding.UTF8.GetString bytes

                    match credentials.Split(':') with
                    | [| _; token |] when token <> "" -> Ok token
                    | _ -> Error BadFormat
        | _ -> Error BadFormat

    let private verifyHeader (header: string) : Result<unit, JwtCryptoError> =
        match b64UrlDecode header with
        | None -> Error BadFormat
        | Some bytes ->
            match Dyn.tryParse (ReadOnlyMemory bytes) with
            | None -> Error BadFormat
            | Some doc ->
                use doc = doc

                match Dyn.stringField "alg" doc.RootElement with
                | Some "HS256" -> Ok()
                | _ -> Error BadFormat

    let private parseClaims (payload: string) : Result<TokenClaims, JwtCryptoError> =
        match b64UrlDecode payload with
        | None -> Error BadFormat
        | Some bytes ->
            match Dyn.tryParse (ReadOnlyMemory bytes) with
            | None -> Error BadFormat
            | Some doc ->
                use doc = doc
                let el = doc.RootElement

                let scopes =
                    Dyn.tryField "scopes" el
                    |> Option.bind Dyn.tryArray
                    |> Option.map (
                        List.choose (fun s ->
                            if s.ValueKind = System.Text.Json.JsonValueKind.String then
                                Some(nonNull (s.GetString()))
                            else
                                None)
                    )

                let user =
                    Dyn.tryField "user" el
                    |> Option.bind Dyn.tryObject
                    |> Option.bind (fun u ->
                        Dyn.stringField "id" u
                        |> Option.map (fun id ->
                            // Optional `name` defaults to `id`, kept in properties
                            // like the Gleam decoder does.
                            let name = Dyn.stringField "name" u |> Option.defaultValue id

                            {
                                Id = id
                                Properties = Map.ofList [ "name", JStr name ]
                            }))

                match
                    Dyn.stringField "documentId" el,
                    Dyn.stringField "tenantId" el,
                    Dyn.intField "exp" el,
                    scopes,
                    user,
                    Dyn.intField "iat" el,
                    Dyn.stringField "ver" el
                with
                | Some doc', Some tenant, Some exp, Some scopeStrings, Some user, Some iat, Some ver ->
                    let claims =
                        {
                            DocumentId = doc'
                            Scopes = scopesFromStrings scopeStrings
                            TenantId = tenant
                            User = user
                            IssuedAt = iat
                            Expiration = exp
                            Version = ver
                            Jti = Dyn.stringField "jti" el
                        }

                    if claims.Version = "1.0" && claims.User.Id <> "" then
                        Ok claims
                    else
                        Error BadFormat
                | _ -> Error BadFormat

    /// Verify an HS256 signature and parse the payload into TokenClaims. Does
    /// not validate tenant/document/expiry — pair with the validate* functions.
    let verifySignature (token: string) (secret: string) : Result<TokenClaims, JwtCryptoError> =
        if secret = "" then
            Error BadSignature
        else
            match token.Split('.') with
            | [| header; payload; signature |] ->
                verifyHeader header
                |> Result.bind (fun () ->
                    let signed = Encoding.UTF8.GetBytes(header + "." + payload)
                    let expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes secret, signed)

                    match b64UrlDecode signature with
                    | Some actual when CryptographicOperations.FixedTimeEquals(actual, expected) ->
                        parseClaims payload
                    | _ -> Error BadSignature)
            | _ -> Error BadFormat

    /// Mint a strict HS256 document token (version "1.0"). `jti` is supplied by
    /// the caller so the function stays pure/deterministic; the host layer
    /// passes 16 random bytes hex-encoded lowercase.
    let mintToken
        (tenant: string)
        (documentId: string)
        (scopes: Scope list)
        (userId: string)
        (secret: string)
        (now: int64)
        (expiresIn: int64)
        (jti: string)
        : string =
        let header =
            b64UrlEncode (Json.toUtf8 (JObj [ "alg", JStr "HS256"; "typ", JStr "JWT" ]))

        let payload =
            b64UrlEncode (
                Json.toUtf8 (
                    JObj
                        [
                            "documentId", JStr documentId
                            "tenantId", JStr tenant
                            "scopes", JArr(scopes |> List.map (scopeToString >> JStr))
                            "user", JObj [ "id", JStr userId ]
                            "ver", JStr "1.0"
                            "iat", JInt now
                            "exp", JInt(now + expiresIn)
                            "jti", JStr jti
                        ]
                )
            )

        let signed = header + "." + payload

        let signature =
            b64UrlEncode (
                HMACSHA256.HashData(Encoding.UTF8.GetBytes secret, Encoding.UTF8.GetBytes signed)
            )

        signed + "." + signature
