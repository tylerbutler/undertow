module Undertow.Protocol.Tests.SiltTests

open Expecto
open Undertow.Protocol
open Undertow.Protocol.Silt

let private dictFetch (objects: Map<string, string>) : Fetch = fun sha -> Map.tryFind sha objects

[<Tests>]
let siltTests =
    testList
        "Silt"
        [
            testList
                "object ids"
                [
                    testCase "blob object id matches git"
                    <| fun () ->
                        // `printf 'blob 5\0hello' | sha1sum` — real git object id.
                        Expect.equal
                            (objectId "blobs" """{"content":"hello","encoding":"utf-8"}""")
                            (Some "b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0")
                            "git id"

                    testCase "blob id from bytes matches git"
                    <| fun () ->
                        Expect.equal
                            (blobId (System.Text.Encoding.UTF8.GetBytes "hello"))
                            "b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0"
                            "git id"

                    testCase "blob object id base64 matches utf8"
                    <| fun () ->
                        Expect.equal
                            (objectId "blobs" """{"content":"aGVsbG8=","encoding":"base64"}""")
                            (Some "b6fc4c620b67d95f953a5c1c1230aaab5db5a1b0")
                            "same bytes, same id"

                    testCase "tree object id is deterministic"
                    <| fun () ->
                        let body = """{"tree":[]}"""
                        let id = objectId "trees" body
                        Expect.isSome id "hashes"
                        Expect.equal (objectId "trees" body) id "stable"

                    testCase "object id rejects unknown kind"
                    <| fun () -> Expect.equal (objectId "tags" "{}") None "rejected"
                ]

            testList
                "decoders"
                [
                    testCase "decode blob"
                    <| fun () ->
                        Expect.equal
                            (decodeBlob """{"content":"hi","encoding":"utf-8"}""")
                            (Some("hi", "utf-8"))
                            "decoded"

                    testCase "decode blob defaults encoding"
                    <| fun () ->
                        Expect.equal
                            (decodeBlob """{"content":"hi"}""")
                            (Some("hi", "utf-8"))
                            "default"

                    testCase "decode tree"
                    <| fun () ->
                        Expect.equal
                            (decodeTree """{"tree":[{"path":"a.txt","type":"blob","sha":"s1"}]}""")
                            (Some
                                [
                                    {
                                        Path = "a.txt"
                                        Mode = "100644"
                                        Kind = "blob"
                                        Size = 0L
                                        Sha = "s1"
                                    }
                                ])
                            "decoded"

                    testCase "decode commit defaults committer to author"
                    <| fun () ->
                        let body =
                            """{"tree":"t1","author":{"name":"Ada","email":"a@x","date":"now"}}"""

                        let ada =
                            {
                                Name = "Ada"
                                Email = "a@x"
                                Date = "now"
                            }

                        Expect.equal
                            (decodeCommit body)
                            (Some
                                {
                                    Tree = "t1"
                                    Parents = []
                                    Message = ""
                                    Author = ada
                                    Committer = ada
                                })
                            "decoded"

                    testCase "decode commit accepts numeric author date"
                    <| fun () ->
                        let body =
                            """{"tree":"t1","author":{"name":"Ada","email":"a@x","date":1767225600}}"""

                        let ada =
                            {
                                Name = "Ada"
                                Email = "a@x"
                                Date = "1767225600"
                            }

                        Expect.equal
                            (decodeCommit body)
                            (Some
                                {
                                    Tree = "t1"
                                    Parents = []
                                    Message = ""
                                    Author = ada
                                    Committer = ada
                                })
                            "normalized to string"

                    testCase "decode ref"
                    <| fun () ->
                        Expect.equal
                            (decodeRef """{"ref":"refs/heads/main","sha":"abc"}""")
                            (Some("refs/heads/main", "abc"))
                            "decoded"
                ]

            testList
                "rest shapes"
                [
                    testCase "normalize ref qualifies bare refs"
                    <| fun () ->
                        Expect.equal (normalizeRef "heads/main") "refs/heads/main" "bare"

                        Expect.equal
                            (normalizeRef "refs/heads/main")
                            "refs/heads/main"
                            "already qualified"

                    testCase "ref response shape"
                    <| fun () ->
                        Expect.equal
                            (Json.toString (refResponse "http://h" "t" "heads/main" "abc"))
                            ("{\"ref\":\"refs/heads/main\","
                             + "\"url\":\"http://h/repos/t/git/refs/heads/main\","
                             + "\"object\":{\"type\":\"commit\",\"sha\":\"abc\","
                             + "\"url\":\"http://h/repos/t/git/commits/abc\"}}")
                            "byte-exact"

                    testCase "blob object response shape"
                    <| fun () ->
                        match
                            objectResponse
                                "http://h"
                                "t"
                                "blobs"
                                "s1"
                                """{"content":"hi","encoding":"utf-8"}"""
                                false
                                (dictFetch Map.empty)
                        with
                        | Some body ->
                            Expect.equal
                                (Json.toString body)
                                ("{\"sha\":\"s1\",\"size\":2,\"content\":\"hi\","
                                 + "\"encoding\":\"utf-8\",\"url\":\"http://h/repos/t/git/blobs/s1\"}")
                                "byte-exact"
                        | None -> failtest "expected Some blob response"

                    testCase "recursive tree response flattens children"
                    <| fun () ->
                        let child = """{"tree":[{"path":"b.txt","type":"blob","sha":"bsha"}]}"""

                        let root =
                            """{"tree":[{"path":"dir","type":"tree","sha":"child"},{"path":"a.txt","type":"blob","sha":"asha"}]}"""

                        match
                            objectResponse
                                "http://h"
                                "t"
                                "trees"
                                "root"
                                root
                                true
                                (dictFetch (Map.ofList [ "child", child ]))
                        with
                        | Some body ->
                            Expect.stringContains
                                (Json.toString body)
                                "\"path\":\"dir/b.txt\""
                                "prefixed path"
                        | None -> failtest "expected Some tree response"

                    testCase "commit history walks parents"
                    <| fun () ->
                        let c1 = """{"tree":"t1","author":{"name":"Ada"}}"""
                        let c2 = """{"tree":"t2","parents":["c1"],"author":{"name":"Ada"}}"""
                        let fetch = dictFetch (Map.ofList [ "c1", c1; "c2", c2 ])

                        Expect.equal
                            (List.length (commitHistoryResponse "http://h" "t" "c2" 10 fetch))
                            2
                            "walked"

                    testCase "commit history respects count"
                    <| fun () ->
                        let c1 = """{"tree":"t1","author":{"name":"Ada"}}"""
                        let c2 = """{"tree":"t2","parents":["c1"],"author":{"name":"Ada"}}"""
                        let fetch = dictFetch (Map.ofList [ "c1", c1; "c2", c2 ])

                        Expect.equal
                            (List.length (commitHistoryResponse "http://h" "t" "c2" 1 fetch))
                            1
                            "capped"
                ]
        ]
