namespace Undertow.Protocol

/// Port of silt: content-addressed git objects (git-canonical SHA-1 ids) and
/// the Historian REST response shapes.
///
/// Two hashing schemes coexist ON PURPOSE (Levee ADR-006): blobs use
/// git-canonical framing ("blob " + byteLen + "\0" + content) so ids match real
/// git; trees and commits hash their serialized JSON body directly. It looks
/// like a bug; it is the contract — existing stored commits and refs hash that
/// way.
module Silt =

    open System
    open System.Security.Cryptography
    open System.Text

    /// Resolve an object body by SHA. The host pre-loads the transitive
    /// closure so this is a pure dictionary lookup, never IO.
    type Fetch = string -> string option

    type Person =
        {
            Name: string
            Email: string
            Date: string
        }

    type TreeEntry =
        {
            Path: string
            Mode: string
            Kind: string
            Size: int64
            Sha: string
        }

    type Commit =
        {
            Tree: string
            Parents: string list
            Message: string
            Author: Person
            Committer: Person
        }

    // ── Hashing ─────────────────────────────────────────────────────────────

    /// Lowercase hex SHA-1 of the given bytes.
    let sha1 (body: byte[]) : string =
        Convert.ToHexString(SHA1.HashData body).ToLowerInvariant()

    /// Git-canonical SHA-1 id for a blob's raw content bytes.
    let blobId (content: byte[]) : string =
        let header = Encoding.UTF8.GetBytes $"blob {content.Length}\000"
        sha1 (Array.append header content)

    /// Decode the raw bytes carried by a blob given its declared encoding.
    let blobContent (content: string) (encoding: string) : byte[] option =
        match encoding with
        | "base64" ->
            try
                Some(Convert.FromBase64String content)
            with _ ->
                None
        | "utf-8" -> Some(Encoding.UTF8.GetBytes content)
        | _ -> None

    // ── Body decoders ───────────────────────────────────────────────────────

    let private parse (body: string) = Dyn.tryParseString body

    /// Decode a blob body into (content, encoding); encoding defaults "utf-8".
    let decodeBlob (body: string) : (string * string) option =
        match parse body with
        | None -> None
        | Some doc ->
            use doc = doc
            let el = doc.RootElement

            match Dyn.stringField "content" el with
            | Some content ->
                let encoding = Dyn.stringField "encoding" el |> Option.defaultValue "utf-8"
                Some(content, encoding)
            | None -> None

    let private decodePerson (el: Text.Json.JsonElement) : Person =
        // `date` is a string in the Historian types, but callers routinely send
        // Unix seconds; accept a number and render it as a string.
        let date =
            match Dyn.tryField "date" el with
            | Some d when d.ValueKind = Text.Json.JsonValueKind.String -> nonNull (d.GetString())
            | Some d when d.ValueKind = Text.Json.JsonValueKind.Number ->
                match d.TryGetInt64() with
                | true, i -> string i
                | _ -> ""
            | _ -> ""

        {
            Name = Dyn.stringField "name" el |> Option.defaultValue ""
            Email = Dyn.stringField "email" el |> Option.defaultValue ""
            Date = date
        }

    /// Decode a tree body into its entries.
    let decodeTree (body: string) : TreeEntry list option =
        match parse body with
        | None -> None
        | Some doc ->
            use doc = doc

            match Dyn.tryField "tree" doc.RootElement |> Option.bind Dyn.tryArray with
            | None -> None
            | Some entries ->
                let decoded =
                    entries
                    |> List.choose (fun e ->
                        match Dyn.stringField "path" e, Dyn.stringField "sha" e with
                        | Some path, Some sha ->
                            Some
                                {
                                    Path = path
                                    Mode = Dyn.stringField "mode" e |> Option.defaultValue "100644"
                                    Kind = Dyn.stringField "type" e |> Option.defaultValue "blob"
                                    Size = Dyn.intField "size" e |> Option.defaultValue 0L
                                    Sha = sha
                                }
                        | _ -> None)

                if List.length decoded = List.length entries then
                    Some decoded
                else
                    None

    /// Decode a commit body.
    let decodeCommit (body: string) : Commit option =
        match parse body with
        | None -> None
        | Some doc ->
            use doc = doc
            let el = doc.RootElement

            match Dyn.stringField "tree" el, Dyn.tryField "author" el with
            | Some tree, Some authorEl when authorEl.ValueKind = Text.Json.JsonValueKind.Object ->
                let author = decodePerson authorEl

                let committer =
                    match Dyn.tryField "committer" el with
                    | Some c when c.ValueKind = Text.Json.JsonValueKind.Object -> decodePerson c
                    | _ -> author

                let parents =
                    Dyn.tryField "parents" el
                    |> Option.bind Dyn.tryArray
                    |> Option.map (
                        List.choose (fun p ->
                            if p.ValueKind = Text.Json.JsonValueKind.String then
                                Some(nonNull (p.GetString()))
                            else
                                None)
                    )
                    |> Option.defaultValue []

                Some
                    {
                        Tree = tree
                        Parents = parents
                        Message = Dyn.stringField "message" el |> Option.defaultValue ""
                        Author = author
                        Committer = committer
                    }
            | _ -> None

    /// Decode a ref-update body into (ref, sha).
    let decodeRef (body: string) : (string * string) option =
        match parse body with
        | None -> None
        | Some doc ->
            use doc = doc
            let el = doc.RootElement

            match Dyn.stringField "ref" el, Dyn.stringField "sha" el with
            | Some ref, Some sha -> Some(ref, sha)
            | _ -> None

    /// Compute the content-addressed id for a body of `kind`
    /// ("blobs" | "trees" | "commits").
    let objectId (kind: string) (body: string) : string option =
        match kind with
        | "blobs" ->
            decodeBlob body
            |> Option.bind (fun (content, encoding) -> blobContent content encoding)
            |> Option.map blobId
        | "trees"
        | "commits" -> Some(sha1 (Encoding.UTF8.GetBytes body))
        | _ -> None

    // ── REST response shapes ────────────────────────────────────────────────

    /// Normalize a ref to its fully-qualified `refs/...` form.
    let normalizeRef (ref: string) : string =
        if ref.StartsWith("refs/", StringComparison.Ordinal) then
            ref
        else
            "refs/" + ref

    let private objectUrl baseUrl tenant kind sha =
        $"{baseUrl}/repos/{tenant}/git/{kind}/{sha}"

    let private personJson (person: Person) =
        JObj
            [
                "name", JStr person.Name
                "email", JStr person.Email
                "date", JStr person.Date
            ]

    let private commitHashJson baseUrl tenant kind sha =
        JObj [ "sha", JStr sha; "url", JStr(objectUrl baseUrl tenant kind sha) ]

    let private blobResponse baseUrl tenant sha body =
        decodeBlob body
        |> Option.bind (fun (content, encoding) ->
            blobContent content encoding
            |> Option.map (fun bytes ->
                JObj
                    [
                        "sha", JStr sha
                        "size", JInt(int64 bytes.Length)
                        "content", JStr content
                        "encoding", JStr encoding
                        "url", JStr(objectUrl baseUrl tenant "blobs" sha)
                    ]))

    /// Flatten nested trees depth-first, capped at `depth` levels.
    let rec private flattenTree
        (fetch: Fetch)
        (prefix: string)
        (entries: TreeEntry list)
        (depth: int)
        =
        entries
        |> List.collect (fun entry ->
            let path = if prefix = "" then entry.Path else $"{prefix}/{entry.Path}"
            let current = { entry with Path = path }

            match entry.Kind, depth > 0 with
            | "tree", true ->
                match fetch entry.Sha with
                | Some body ->
                    match decodeTree body with
                    | Some children -> current :: flattenTree fetch path children (depth - 1)
                    | None -> [ current ]
                | None -> [ current ]
            | _ -> [ current ])

    let private treeResponse baseUrl tenant sha body recursive fetch =
        decodeTree body
        |> Option.map (fun entries ->
            let entries =
                if recursive then
                    flattenTree fetch "" entries 64
                else
                    entries

            JObj
                [
                    "sha", JStr sha
                    "url", JStr(objectUrl baseUrl tenant "trees" sha)
                    "tree",
                    JArr(
                        entries
                        |> List.map (fun entry ->
                            JObj
                                [
                                    "path", JStr entry.Path
                                    "mode", JStr entry.Mode
                                    "type", JStr entry.Kind
                                    "size", JInt entry.Size
                                    "sha", JStr entry.Sha
                                    "url",
                                    JStr(objectUrl baseUrl tenant (entry.Kind + "s") entry.Sha)
                                ])
                    )
                ])

    let private commitResponse baseUrl tenant sha body =
        decodeCommit body
        |> Option.map (fun commit ->
            JObj
                [
                    "sha", JStr sha
                    "url", JStr(objectUrl baseUrl tenant "commits" sha)
                    "author", personJson commit.Author
                    "committer", personJson commit.Committer
                    "message", JStr commit.Message
                    "tree", commitHashJson baseUrl tenant "trees" commit.Tree
                    "parents",
                    JArr(commit.Parents |> List.map (commitHashJson baseUrl tenant "commits"))
                ])

    /// Build the REST response for a stored object of `kind`.
    let objectResponse baseUrl tenant kind sha body recursive (fetch: Fetch) : Json option =
        match kind with
        | "blobs" -> blobResponse baseUrl tenant sha body
        | "trees" -> treeResponse baseUrl tenant sha body recursive fetch
        | "commits" -> commitResponse baseUrl tenant sha body
        | _ -> None

    let private commitDetailsJson baseUrl tenant sha (commit: Commit) =
        let commitUrl = objectUrl baseUrl tenant "commits" sha

        JObj
            [
                "sha", JStr sha
                "url", JStr commitUrl
                "commit",
                JObj
                    [
                        "url", JStr commitUrl
                        "author", personJson commit.Author
                        "committer", personJson commit.Committer
                        "message", JStr commit.Message
                        "tree", commitHashJson baseUrl tenant "trees" commit.Tree
                    ]
                "parents",
                JArr(commit.Parents |> List.map (commitHashJson baseUrl tenant "commits"))
            ]

    /// Build the detailed commit response for a single commit body.
    let commitDetailsResponse baseUrl tenant sha body : Json option =
        decodeCommit body |> Option.map (commitDetailsJson baseUrl tenant sha)

    /// Build the commit-history response, walking first parents up to `count`.
    let rec commitHistoryResponse baseUrl tenant sha (count: int) (fetch: Fetch) : Json list =
        if count <= 0 then
            []
        else
            match fetch sha with
            | None -> []
            | Some body ->
                match decodeCommit body with
                | None -> []
                | Some commit ->
                    commitDetailsJson baseUrl tenant sha commit
                    :: (match commit.Parents with
                        | parent :: _ ->
                            commitHistoryResponse baseUrl tenant parent (count - 1) fetch
                        | [] -> [])

    /// Build a ref response for `ref` pointing at commit `sha`.
    let refResponse baseUrl tenant ref sha : Json =
        let ref = normalizeRef ref

        JObj
            [
                "ref", JStr ref
                "url", JStr $"{baseUrl}/repos/{tenant}/git/refs/{ref.Substring 5}"
                "object",
                JObj
                    [
                        "type", JStr "commit"
                        "sha", JStr sha
                        "url", JStr(objectUrl baseUrl tenant "commits" sha)
                    ]
            ]
