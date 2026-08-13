namespace Undertow.Protocol

// The F#/C# boundary: F# discriminated unions and options never cross.
// Everything here trades in records with plain fields, arrays, enums, and
// nullable strings, converted at this edge.

open System
open System.Collections.Generic

/// Outcome of an authorization check. ErrorMessage is "" when Ok.
type AuthOutcome = { Ok: bool; ErrorMessage: string }

/// The six route-family authorization checks, mirroring the Gleam router's
/// authorize_* helpers: the tenant-vs-configured-tenant check and the
/// missing-header distinction included.
module AuthBoundary =

    let private outcome (result: Result<'a, Auth.AuthError>) : AuthOutcome =
        match result with
        | Ok _ -> { Ok = true; ErrorMessage = "" }
        | Error e ->
            {
                Ok = false
                ErrorMessage = Auth.errorMessage e
            }

    let private failWith e : AuthOutcome =
        {
            Ok = false
            ErrorMessage = Auth.errorMessage e
        }

    /// The header + tenant gate both route families share: a wrong tenant is a
    /// (401-reported) tenant mismatch, a missing header is MissingAuthorization.
    let private gate
        (configuredTenant: string)
        (tenant: string)
        (hasHeader: bool)
        (verify: string -> Result<Signet.TokenClaims, Auth.AuthError>)
        (authorization: string)
        : AuthOutcome =
        if tenant <> configuredTenant then
            failWith (Auth.BadClaims(Signet.TenantMismatch(configuredTenant, tenant)))
        elif not hasHeader then
            failWith Auth.MissingAuthorization
        else
            outcome (verify authorization)

    let tenantWrite
        (
            configuredTenant: string,
            tenant: string,
            hasHeader: bool,
            authorization: string,
            secret: string,
            now: int64
        ) =
        gate
            configuredTenant
            tenant
            hasHeader
            (fun a -> Auth.verifyTenantWriteAuthorization a secret tenant now)
            authorization

    let write
        (
            configuredTenant: string,
            tenant: string,
            doc: string,
            hasHeader: bool,
            authorization: string,
            secret: string,
            now: int64
        ) =
        gate
            configuredTenant
            tenant
            hasHeader
            (fun a -> Auth.verifyWriteAuthorization a secret tenant doc now)
            authorization

    let read
        (
            configuredTenant: string,
            tenant: string,
            doc: string,
            hasHeader: bool,
            authorization: string,
            secret: string,
            now: int64
        ) =
        gate
            configuredTenant
            tenant
            hasHeader
            (fun a -> Auth.verifyReadAuthorization a secret tenant doc now)
            authorization

    let storageRead
        (
            configuredTenant: string,
            tenant: string,
            hasHeader: bool,
            authorization: string,
            secret: string,
            now: int64
        ) =
        gate
            configuredTenant
            tenant
            hasHeader
            (fun a -> Auth.verifyStorageReadAuthorization a secret tenant now)
            authorization

    let storageWrite
        (
            configuredTenant: string,
            tenant: string,
            hasHeader: bool,
            authorization: string,
            secret: string,
            now: int64
        ) =
        gate
            configuredTenant
            tenant
            hasHeader
            (fun a -> Auth.verifyStorageWriteAuthorization a secret tenant now)
            authorization

    let tokenMint (authorization: string, expectedSecret: string) : bool =
        Auth.verifyTokenMintAuthorization authorization expectedSecret

    /// Mint a document token (the token-mint endpoint's payload).
    let mintToken
        (
            tenant: string,
            documentId: string,
            scopes: string[],
            userId: string,
            secret: string,
            now: int64,
            expiresIn: int64,
            jti: string
        ) =
        Signet.mintToken
            tenant
            documentId
            (Signet.scopesFromStrings (List.ofArray scopes))
            userId
            secret
            now
            expiresIn
            jti

/// Historian REST shapes for C# callers: fetches resolve against a pre-loaded
/// dictionary (the transitive closure), so no IO crosses this boundary.
module SiltBoundary =

    let private dictFetch (objects: IReadOnlyDictionary<string, string>) : Silt.Fetch =
        fun sha ->
            match objects.TryGetValue sha with
            | true, body -> Some body
            | _ -> None

    /// UTF-8 JSON of the object response, or null when the body is invalid.
    let objectResponse
        (
            baseUrl: string,
            tenant: string,
            kind: string,
            sha: string,
            body: string,
            recursive: bool,
            objects: IReadOnlyDictionary<string, string>
        ) : byte[] | null =
        match Silt.objectResponse baseUrl tenant kind sha body recursive (dictFetch objects) with
        | Some json -> Json.toUtf8 json
        | None -> null

    /// UTF-8 JSON array of commit-history entries.
    let commitHistoryResponse
        (
            baseUrl: string,
            tenant: string,
            sha: string,
            count: int,
            objects: IReadOnlyDictionary<string, string>
        ) : byte[] =
        Json.toUtf8 (JArr(Silt.commitHistoryResponse baseUrl tenant sha count (dictFetch objects)))

    let refResponse (baseUrl: string, tenant: string, ref: string, sha: string) : byte[] =
        Json.toUtf8 (Silt.refResponse baseUrl tenant ref sha)

    /// Content-addressed id for a body of kind, or null for an unknown kind
    /// or invalid body.
    let objectId (kind: string, body: string) : string | null =
        match Silt.objectId kind body with
        | Some sha -> sha
        | None -> null

    let normalizeRef (ref: string) = Silt.normalizeRef ref

    /// Decode a ref-update body; false when invalid.
    let tryDecodeRef (body: string, ref: byref<string>, sha: byref<string>) : bool =
        match Silt.decodeRef body with
        | Some(r, s) ->
            ref <- r
            sha <- s
            true
        | None -> false

    /// Tree-sha children of a tree body (for pre-loading the closure).
    let treeChildShas (body: string) : string[] =
        match Silt.decodeTree body with
        | Some entries ->
            entries
            |> List.filter (fun e -> e.Kind = "tree")
            |> List.map (fun e -> e.Sha)
            |> Array.ofList
        | None -> [||]

    /// First parent of a commit body, or null.
    let commitFirstParent (body: string) : string | null =
        match Silt.decodeCommit body with
        | Some { Parents = parent :: _ } -> parent
        | _ -> null

/// InitialSummary planning for C# callers.
type InitialSummaryStatus =
    | NoSummary = 0
    | Planned = 1
    | Invalid = 2

type InitialSummaryPlan =
    {
        Status: InitialSummaryStatus
        /// (sha, body) pairs, children before parents.
        Objects: KeyValuePair<string, string>[]
        CommitSha: string
        SequenceNumber: int64
    }

module InitialSummaryBoundary =

    let plan (body: string, timestamp: int64, exists: Func<string, bool>) : InitialSummaryPlan =
        match InitialSummary.plan body timestamp exists.Invoke with
        | Error() ->
            {
                Status = InitialSummaryStatus.Invalid
                Objects = [||]
                CommitSha = ""
                SequenceNumber = 0L
            }
        | Ok None ->
            {
                Status = InitialSummaryStatus.NoSummary
                Objects = [||]
                CommitSha = ""
                SequenceNumber = 0L
            }
        | Ok(Some plan) ->
            {
                Status = InitialSummaryStatus.Planned
                Objects = plan.Objects |> List.map KeyValuePair |> Array.ofList
                CommitSha = plan.CommitSha
                SequenceNumber = plan.SequenceNumber
            }
