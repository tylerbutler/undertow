namespace Undertow.Protocol

/// Port of floodgate/initial_summary: turns Routerlicious' document-create
/// whole-summary payload into the shredded Historian object graph.
///
/// Pure planner: instead of writing to storage during traversal, it computes
/// every (sha, body) pair (children before parents) plus the root commit sha.
/// The one effect the Gleam version needs mid-walk — verifying that an
/// id-referenced object exists — is injected as `exists`.
module InitialSummary =

    open System.Text.Json

    type SummaryValue =
        | SummaryTree of entries: SummaryEntry list
        | SummaryBlob of content: string * encoding: string

    and SummaryEntry =
        {
            Path: string
            Kind: string
            Value: SummaryValue option
            Id: string option
        }

    type Plan =
        {
            Objects: (string * string) list
            CommitSha: string
            SequenceNumber: int64
        }

    // ── Payload decoding (two wire shapes, ADR-008) ─────────────────────────

    let private jsonEncode (el: JsonElement) : string = el.GetRawText()

    /// ISummaryBlob.content is string | Uint8Array | plain JSON — re-encode
    /// anything non-string rather than rejecting the summary.
    let private blobContentOf (el: JsonElement) : string option =
        match Dyn.tryField "content" el with
        | Some c when c.ValueKind = JsonValueKind.String -> Some(nonNull (c.GetString()))
        | Some c -> Some(jsonEncode c)
        | None -> None

    /// Routerlicious whole-summary: string `type`, `entries` array.
    /// Fluid ISummaryTree: numeric `type` (Tree=1, Blob=2), `tree` map by path.
    let rec private decodeValue (el: JsonElement) : SummaryValue option =
        if el.ValueKind <> JsonValueKind.Object then
            None
        else
            match Dyn.tryField "type" el with
            | Some t when t.ValueKind = JsonValueKind.String ->
                match t.GetString() with
                | "tree" ->
                    let entries =
                        Dyn.tryField "entries" el
                        |> Option.bind Dyn.tryArray
                        |> Option.map (List.map decodeEntry)

                    match entries with
                    | Some entries when List.forall Option.isSome entries ->
                        Some(SummaryTree(List.choose id entries))
                    | Some _ -> None
                    | None -> Some(SummaryTree [])
                | "blob" ->
                    match Dyn.stringField "content" el with
                    | Some content ->
                        let encoding = Dyn.stringField "encoding" el |> Option.defaultValue "utf-8"
                        Some(SummaryBlob(content, encoding))
                    | None -> None
                | _ -> None
            | Some t when t.ValueKind = JsonValueKind.Number ->
                match t.TryGetInt32() with
                | true, 1 ->
                    // Sorted so the resulting tree object is deterministic.
                    let tree =
                        Dyn.tryField "tree" el
                        |> Option.bind Dyn.tryObject
                        |> Option.map (fun o ->
                            o.EnumerateObject()
                            |> Seq.map (fun p -> p.Name, decodeValue p.Value)
                            |> Seq.toList)
                        |> Option.defaultValue []

                    if tree |> List.forall (snd >> Option.isSome) then
                        tree
                        |> List.map (fun (path, v) -> path, Option.get v)
                        |> List.sortWith (fun (a, _) (b, _) -> System.String.CompareOrdinal(a, b))
                        |> List.map (fun (path, value) ->
                            let kind =
                                match value with
                                | SummaryTree _ -> "tree"
                                | SummaryBlob _ -> "blob"

                            {
                                Path = path
                                Kind = kind
                                Value = Some value
                                Id = None
                            })
                        |> SummaryTree
                        |> Some
                    else
                        None
                | true, 2 -> blobContentOf el |> Option.map (fun c -> SummaryBlob(c, "utf-8"))
                | _ -> None
            | _ -> None

    and private decodeEntry (el: JsonElement) : SummaryEntry option =
        match Dyn.stringField "path" el, Dyn.stringField "type" el with
        | Some path, Some kind ->
            let value =
                match Dyn.tryField "value" el with
                | Some v when v.ValueKind <> JsonValueKind.Null -> decodeValue v
                | _ -> None

            let hasBadValue =
                match Dyn.tryField "value" el with
                | Some v when v.ValueKind <> JsonValueKind.Null -> Option.isNone value
                | _ -> false

            if hasBadValue then
                None
            else
                Some
                    {
                        Path = path
                        Kind = kind
                        Value = value
                        Id = Dyn.stringField "id" el
                    }
        | _ -> None

    // ── Planning (pure content-addressed writes) ────────────────────────────

    let private mode kind =
        match kind with
        | "blob" -> Some "100644"
        | "tree" -> Some "040000"
        | "commit" -> Some "160000"
        | _ -> None

    type private Builder =
        {
            mutable Objects: (string * string) list
        }

    let private storeObject (b: Builder) (kind: string) (body: string) : string option =
        Silt.objectId kind body
        |> Option.map (fun sha ->
            b.Objects <- (sha, body) :: b.Objects
            sha)

    let private treeEntryJson path kind sha : Json option =
        mode kind
        |> Option.map (fun m ->
            JObj [ "path", JStr path; "mode", JStr m; "type", JStr kind; "sha", JStr sha ])

    let private storeTreeEntries (b: Builder) (entries: Json list) : string option =
        storeObject b "trees" (Json.toString (JObj [ "tree", JArr entries ]))

    let rec private storeValue
        (b: Builder)
        (exists: string -> bool)
        (value: SummaryValue)
        : string option =
        match value with
        | SummaryTree entries -> storeTree b exists entries
        | SummaryBlob(content, encoding) ->
            storeObject
                b
                "blobs"
                (Json.toString (JObj [ "content", JStr content; "encoding", JStr encoding ]))

    and private storeEntry
        (b: Builder)
        (exists: string -> bool)
        (entry: SummaryEntry)
        : Json option =
        let sha =
            match entry.Value, entry.Id with
            | Some value, None -> storeValue b exists value
            | None, Some id -> if exists id then Some id else None
            | _ -> None

        sha |> Option.bind (treeEntryJson entry.Path entry.Kind)

    and private storeTree
        (b: Builder)
        (exists: string -> bool)
        (entries: SummaryEntry list)
        : string option =
        let stored = entries |> List.map (storeEntry b exists)

        if List.forall Option.isSome stored then
            storeTreeEntries b (List.choose id stored)
        else
            None

    let private storeProtocol
        (b: Builder)
        (sequenceNumber: int64)
        (values: JsonElement option)
        : string option =
        let attributes =
            Json.toString (
                JObj
                    [
                        "minimumSequenceNumber", JInt sequenceNumber
                        "sequenceNumber", JInt sequenceNumber
                    ]
            )

        let valuesJson =
            match values with
            | Some v -> v.GetRawText()
            | None -> "[]"

        let blob content =
            storeObject
                b
                "blobs"
                (Json.toString (JObj [ "content", JStr content; "encoding", JStr "utf-8" ]))

        match blob attributes, blob valuesJson, blob "[]", blob "[]" with
        | Some attributesSha, Some quorumValuesSha, Some quorumMembersSha, Some quorumProposalsSha ->
            [
                treeEntryJson "attributes" "blob" attributesSha
                treeEntryJson "quorumMembers" "blob" quorumMembersSha
                treeEntryJson "quorumProposals" "blob" quorumProposalsSha
                treeEntryJson "quorumValues" "blob" quorumValuesSha
            ]
            |> List.choose id
            |> storeTreeEntries b
        | _ -> None

    let private findEntry (entries: SummaryEntry list) (path: string) : SummaryValue option =
        entries
        |> List.tryFind (fun e -> e.Path = path)
        |> Option.bind (fun e -> e.Value)

    /// The official driver posts `.app` contents as `summary` plus quorum in
    /// `values` (protocol tree synthesized here, root = .app + .protocol);
    /// levee-driver posts the whole combined ISummaryTree (`.app`'s children
    /// flattened to the root, its `.protocol` stored beside them).
    let private storeRootTree
        (b: Builder)
        (exists: string -> bool)
        (entries: SummaryEntry list)
        (sequenceNumber: int64)
        (values: JsonElement option)
        : string option =
        match findEntry entries ".app" with
        | Some(SummaryTree appEntries) ->
            let appTreeEntries = appEntries |> List.map (storeEntry b exists)

            if not (List.forall Option.isSome appTreeEntries) then
                None
            else
                let protocolEntries =
                    match findEntry entries ".protocol" with
                    | Some(SummaryTree _ as protocol) ->
                        storeValue b exists protocol
                        |> Option.bind (treeEntryJson ".protocol" "tree")
                        |> Option.map List.singleton
                    | _ -> Some []

                protocolEntries
                |> Option.bind (fun protocol ->
                    storeTreeEntries b (List.choose id appTreeEntries @ protocol))
        | _ ->
            match storeTree b exists entries, storeProtocol b sequenceNumber values with
            | Some appTreeSha, Some protocolTreeSha ->
                [
                    treeEntryJson ".app" "tree" appTreeSha
                    treeEntryJson ".protocol" "tree" protocolTreeSha
                ]
                |> List.choose id
                |> storeTreeEntries b
            | _ -> None

    /// Plan the Historian objects for a create payload. Ok None: no summary in
    /// the body. Error: the payload carried a summary that is invalid.
    let plan
        (body: string)
        (timestamp: int64)
        (exists: string -> bool)
        : Result<Plan option, unit> =
        if body.Trim() = "" then
            Ok None
        else
            match Dyn.tryParseString body with
            | None -> Error()
            | Some doc ->
                use doc = doc
                let root = doc.RootElement

                if root.ValueKind <> JsonValueKind.Object then
                    Error()
                else
                    let summaryField = Dyn.tryField "summary" root

                    match summaryField with
                    | None -> Ok None
                    | Some s when s.ValueKind = JsonValueKind.Null -> Ok None
                    | Some s ->
                        match decodeValue s with
                        | None
                        | Some(SummaryBlob _) -> Error()
                        | Some(SummaryTree entries) ->
                            let sequenceNumber =
                                Dyn.intField "sequenceNumber" root |> Option.defaultValue 0L

                            let values = Dyn.tryField "values" root
                            let b = { Objects = [] }

                            match storeRootTree b exists entries sequenceNumber values with
                            | None -> Error()
                            | Some treeSha ->
                                let author =
                                    JObj
                                        [
                                            "name", JStr "Floodgate"
                                            "email", JStr "server@floodgate.local"
                                            "date", JStr(string timestamp)
                                        ]

                                let commitBody =
                                    Json.toString (
                                        JObj
                                            [
                                                "tree", JStr treeSha
                                                "parents", JArr []
                                                "message", JStr "Initial summary"
                                                "author", author
                                                "committer", author
                                            ]
                                    )

                                match storeObject b "commits" commitBody with
                                | None -> Error()
                                | Some commitSha ->
                                    Ok(
                                        Some
                                            {
                                                Objects = List.rev b.Objects
                                                CommitSha = commitSha
                                                SequenceNumber = sequenceNumber
                                            }
                                    )
