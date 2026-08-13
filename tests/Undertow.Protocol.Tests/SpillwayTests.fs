module Undertow.Protocol.Tests.SpillwayTests

open Expecto
open Undertow.Protocol
open Undertow.Protocol.Sequencing

[<Tests>]
let sequencingTests =
    testList
        "Sequencing"
        [
            testCase "new sequence state starts at zero"
            <| fun () ->
                let state = Sequencing.create ()
                Expect.equal (currentSn state) 0L "sn"
                Expect.equal (currentMsn state) 0L "msn"
                Expect.equal (clientCount state) 0 "clients"

            testCase "client join"
            <| fun () ->
                let state = clientJoin (Sequencing.create ()) "client-1" 0L
                Expect.equal (clientCount state) 1 "count"
                Expect.isTrue (isClientConnected state "client-1") "c1"
                Expect.isFalse (isClientConnected state "client-2") "c2"

            testCase "assign sequence number"
            <| fun () ->
                let state = clientJoin (Sequencing.create ()) "client-1" 0L

                match assignSequenceNumber state "client-1" 1L 0L with
                | SequenceOk(newState, assignedSn, msn) ->
                    Expect.equal assignedSn 1L "sn"
                    Expect.equal msn 0L "msn"
                    Expect.equal (currentSn newState) 1L "state sn"
                | other -> failtest $"expected SequenceOk, got {other}"

            testCase "multiple ops increment sn"
            <| fun () ->
                let state = clientJoin (Sequencing.create ()) "client-1" 0L

                match assignSequenceNumber state "client-1" 1L 0L with
                | SequenceOk(state, sn, _) ->
                    Expect.equal sn 1L "first"

                    match assignSequenceNumber state "client-1" 2L 1L with
                    | SequenceOk(state, sn, _) ->
                        Expect.equal sn 2L "second"
                        Expect.equal (currentSn state) 2L "state"
                    | other -> failtest $"expected SequenceOk, got {other}"
                | other -> failtest $"expected SequenceOk, got {other}"

            testCase "invalid csn rejected"
            <| fun () ->
                let state = clientJoin (Sequencing.create ()) "client-1" 0L

                match assignSequenceNumber state "client-1" 1L 0L with
                | SequenceOk(state, _, _) ->
                    match assignSequenceNumber state "client-1" 1L 1L with
                    | SequenceError(InvalidCsn _) -> ()
                    | other -> failtest $"expected InvalidCsn, got {other}"
                | other -> failtest $"expected SequenceOk, got {other}"

            testCase "unknown client rejected before csn"
            <| fun () ->
                let state = Sequencing.create ()

                match assignSequenceNumber state "ghost" 0L 99L with
                | SequenceError(UnknownClient "ghost") -> ()
                | other -> failtest $"expected UnknownClient, got {other}"

            testCase "invalid rsn rejected"
            <| fun () ->
                let state = clientJoin (Sequencing.create ()) "client-1" 0L

                match assignSequenceNumber state "client-1" 1L 5L with
                | SequenceError(InvalidRsn(currentSn, receivedRsn)) ->
                    Expect.equal currentSn 0L "sn"
                    Expect.equal receivedRsn 5L "rsn"
                | other -> failtest $"expected InvalidRsn, got {other}"

            testCase "client leave"
            <| fun () ->
                let state =
                    clientJoin (clientJoin (Sequencing.create ()) "client-1" 0L) "client-2" 0L

                Expect.equal (clientCount state) 2 "before"
                let state = clientLeave state "client-1"
                Expect.equal (clientCount state) 1 "after"
                Expect.isFalse (isClientConnected state "client-1") "gone"
                Expect.isTrue (isClientConnected state "client-2") "stays"

            testCase "msn tracks minimum rsn across clients"
            <| fun () ->
                let state = clientJoin (Sequencing.create ()) "client-1" 0L

                match assignSequenceNumber state "client-1" 1L 0L with
                | SequenceOk(state, _, msn) ->
                    Expect.equal msn 0L "single client"
                    let state = clientJoin state "client-2" 1L

                    match assignSequenceNumber state "client-2" 1L 1L with
                    | SequenceOk(state, _, msn) ->
                        Expect.equal msn 0L "still pinned by client-1 rsn 0"

                        match assignSequenceNumber state "client-1" 2L 2L with
                        | SequenceOk(_, _, msn) -> Expect.equal msn 1L "advances to min(2,1)"
                        | other -> failtest $"expected SequenceOk, got {other}"
                    | other -> failtest $"expected SequenceOk, got {other}"
                | other -> failtest $"expected SequenceOk, got {other}"

            testCase "reserve sequence number advances"
            <| fun () ->
                let state = clientJoin (Sequencing.create ()) "client-1" 0L
                let state, reserved = reserveSequenceNumber state
                Expect.equal reserved 1L "reserved"
                Expect.equal (currentSn state) 1L "advanced"

                match assignSequenceNumber state "client-1" 1L 0L with
                | SequenceOk(_, sn, _) -> Expect.equal sn 2L "next op does not collide"
                | other -> failtest $"expected SequenceOk, got {other}"

            testCase "update client rsn only increases"
            <| fun () ->
                let state = clientJoin (Sequencing.create ()) "client-1" 5L

                match updateClientRsn state "client-1" 3L with
                | Ok state -> Expect.equal (currentMsn state) 5L "rsn did not regress"
                | Error e -> failtest $"expected Ok, got {e}"
        ]

[<Tests>]
let sessionLogicTests =
    testList
        "SessionLogic"
        [
            testList
                "feature negotiation"
                [
                    testCase "both support"
                    <| fun () ->
                        let result =
                            SessionLogic.negotiateFeatures
                                (Map.ofList [ "submit_signals_v2", true ])
                                (Map.ofList [ "submit_signals_v2", true ])

                        Expect.equal (Map.tryFind "submit_signals_v2" result) (Some true) "true"

                    testCase "client declines"
                    <| fun () ->
                        let result =
                            SessionLogic.negotiateFeatures
                                (Map.ofList [ "submit_signals_v2", true ])
                                (Map.ofList [ "submit_signals_v2", false ])

                        Expect.equal (Map.tryFind "submit_signals_v2" result) (Some false) "false"

                    testCase "client unspecified"
                    <| fun () ->
                        let result =
                            SessionLogic.negotiateFeatures
                                (Map.ofList [ "submit_signals_v2", true ])
                                Map.empty

                        Expect.equal
                            (Map.tryFind "submit_signals_v2" result)
                            (Some true)
                            "advertised"
                ]

            testList
                "version negotiation"
                [
                    testCase "match 0.1.0"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.negotiateVersion [ "^0.1.0"; "^1.0.0" ] [ "^0.1.0" ])
                            "0.1.0"
                            ""

                    testCase "match 1.0.0"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.negotiateVersion [ "^0.1.0"; "^1.0.0" ] [ "^1.0.0" ])
                            "1.0.0"
                            ""

                    testCase "fallback"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.negotiateVersion [ "^0.1.0"; "^1.0.0" ] [ "^2.0.0" ])
                            "0.1.0"
                            ""

                    testCase "empty client list"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.negotiateVersion [ "^0.1.0"; "^1.0.0" ] [])
                            "0.1.0"
                            ""
                ]

            testList
                "validate summarize contents"
                [
                    testCase "all present"
                    <| fun () ->
                        let contents =
                            Map.ofList
                                [
                                    "handle", JStr "h1"
                                    "message", JStr "msg"
                                    "parents", JArr []
                                    "head", JStr "sha"
                                ]

                        Expect.isOk (SessionLogic.validateSummarizeContents contents) "ok"

                    testCase "missing fields listed"
                    <| fun () ->
                        match
                            SessionLogic.validateSummarizeContents (
                                Map.ofList [ "handle", JStr "h1" ]
                            )
                        with
                        | Error msg ->
                            Expect.stringContains msg "message" "message"
                            Expect.stringContains msg "parents" "parents"
                            Expect.stringContains msg "head" "head"
                        | Ok() -> failtest "expected Error"
                ]

            testList
                "signal recipients"
                [
                    testCase "broadcast"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.determineSignalRecipients
                                "sender"
                                None
                                None
                                None
                                [ "sender"; "a"; "b"; "c" ])
                            [ "a"; "b"; "c" ]
                            ""

                    testCase "targeted"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.determineSignalRecipients
                                "sender"
                                (Some [ "a"; "c" ])
                                None
                                None
                                [ "sender"; "a"; "b"; "c" ])
                            [ "a"; "c" ]
                            ""

                    testCase "targeted intersects with known clients"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.determineSignalRecipients
                                "sender"
                                (Some [ "a"; "ghost" ])
                                None
                                None
                                [ "sender"; "a"; "b" ])
                            [ "a" ]
                            "unknown target dropped"

                    testCase "ignored"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.determineSignalRecipients
                                "sender"
                                None
                                (Some [ "b" ])
                                None
                                [ "sender"; "a"; "b"; "c" ])
                            [ "a"; "c" ]
                            ""

                    testCase "single target"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.determineSignalRecipients
                                "sender"
                                None
                                None
                                (Some "b")
                                [ "sender"; "a"; "b"; "c" ])
                            [ "b" ]
                            ""

                    testCase "single target is sender"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.determineSignalRecipients
                                "sender"
                                None
                                None
                                (Some "sender")
                                [ "sender"; "a"; "b" ])
                            []
                            ""

                    testCase "targeted excludes sender"
                    <| fun () ->
                        Expect.equal
                            (SessionLogic.determineSignalRecipients
                                "sender"
                                (Some [ "sender"; "a" ])
                                None
                                None
                                [ "sender"; "a"; "b" ])
                            [ "a" ]
                            ""
                ]

            testList
                "history"
                [
                    testCase "prepends"
                    <| fun () ->
                        Expect.equal (SessionLogic.addToHistory 3 [ 2; 1 ] 10) [ 3; 2; 1 ] ""

                    testCase "trims to max"
                    <| fun () ->
                        Expect.equal (SessionLogic.addToHistory 4 [ 3; 2; 1 ] 3) [ 4; 3; 2 ] ""
                ]
        ]

[<Tests>]
let nackTests =
    testList
        "Nack"
        [
            testCase "unknown client"
            <| fun () ->
                let n = Nack.unknownClient "client-42"
                Expect.equal n.Content.Code 400 "code"
                Expect.equal n.Content.Message "Unknown client: client-42" "message"
                Expect.equal n.SequenceNumber -1L "sn"

            testCase "read only client"
            <| fun () ->
                let n = Nack.readOnlyClient None
                Expect.equal n.Content.Code 400 "code"
                Expect.equal n.Content.Message "Client is in read-only mode" "message"

            testCase "invalid csn"
            <| fun () ->
                let n = Nack.invalidCsn 5L 3L None
                Expect.equal n.Content.Code 400 "code"

                Expect.equal
                    n.Content.Message
                    "Invalid client sequence number: expected > 5, received 3"
                    "exact message"

            testCase "invalid rsn"
            <| fun () ->
                let n = Nack.invalidRsn 10L 5L None
                Expect.equal n.Content.Code 400 "code"

                Expect.equal
                    n.Content.Message
                    "Invalid RSN: current SN is 10, received 5"
                    "exact message"

            testCase "error type roundtrip"
            <| fun () ->
                Expect.equal
                    (Nack.nackErrorTypeFromString (Nack.nackErrorTypeToString Nack.BadRequestError))
                    (Some Nack.BadRequestError)
                    "bad request"

                Expect.equal
                    (Nack.nackErrorTypeFromString (Nack.nackErrorTypeToString Nack.ThrottlingError))
                    (Some Nack.ThrottlingError)
                    "throttling"
        ]

[<Tests>]
let signalNormalizationTests =
    testList
        "Signals"
        [
            testCase "normalize v2 signal"
            <| fun () ->
                let raw =
                    Map.ofList
                        [
                            "content", JStr "hello"
                            "type", JStr "myType"
                            "clientConnectionNumber", JInt 42L
                        ]

                let result = Signals.normalizeSignal raw
                Expect.equal result.SignalType (Some "myType") "type"
                Expect.equal result.ClientConnectionNumber (Some 42L) "conn num"
                Expect.equal result.TargetClientId None "no target"

            testCase "normalize v1 signal"
            <| fun () ->
                let raw =
                    Map.ofList
                        [
                            "address", JStr ""
                            "contents", JObj [ "type", JStr "v1Type"; "content", JStr "data" ]
                            "clientBroadcastSignalSequenceNumber", JInt 7L
                        ]

                let result = Signals.normalizeSignal raw
                Expect.equal result.SignalType (Some "v1Type") "type"
                Expect.equal result.ClientConnectionNumber (Some 7L) "conn num"
                Expect.equal result.TargetedClients None "no targeting"

            testCase "normalize v2 envelope signal"
            <| fun () ->
                let raw =
                    Map.ofList
                        [
                            "signal", JObj [ "content", JStr "payload"; "type", JStr "sigType" ]
                            "targetedClients", JArr [ JStr "a"; JStr "b" ]
                        ]

                let result = Signals.normalizeSignal raw
                Expect.equal result.SignalType (Some "sigType") "type"
                Expect.equal result.TargetedClients (Some [ "a"; "b" ]) "targets"

            testCase "normalize batch of maps"
            <| fun () ->
                let batch = JArr [ JObj [ "content", JStr "x" ]; JObj [ "content", JStr "y" ] ]
                Expect.equal (List.length (Signals.normalizeSignalBatch batch)) 2 "two"

            testCase "normalize batch single map"
            <| fun () ->
                Expect.equal
                    (List.length (Signals.normalizeSignalBatch (JObj [ "content", JStr "x" ])))
                    1
                    "one"

            testCase "normalize batch invalid"
            <| fun () -> Expect.equal (Signals.normalizeSignalBatch (JStr "nope")) [] "empty"
        ]
