module Undertow.Protocol.Tests.SequencingProperties

open Expecto
open FsCheck
open Undertow.Protocol
open Undertow.Protocol.Sequencing

/// A random session action; interpreted against a live SequenceState.
type Action =
    | Join of clientIx: int
    | Leave of clientIx: int
    | Submit of clientIx: int * rsnDelta: int
    | Noop of clientIx: int * rsnDelta: int

let private clientName ix = $"client-{abs ix % 5}"

type Generators =
    static member Action() =
        Gen.oneof
            [
                Gen.map Join (Gen.choose (0, 4))
                Gen.map Leave (Gen.choose (0, 4))
                Gen.map2 (fun c d -> Submit(c, d)) (Gen.choose (0, 4)) (Gen.choose (0, 3))
                Gen.map2 (fun c d -> Noop(c, d)) (Gen.choose (0, 4)) (Gen.choose (0, 3))
            ]
        |> Arb.fromGen

let private config =
    { FsCheckConfig.defaultConfig with
        maxTest = 500
        arbitrary = [ typeof<Generators> ]
    }

/// Replay actions, tracking per-client CSNs so submissions are wire-legal.
/// Returns every intermediate (state, assignedSn option).
let private replay (actions: Action list) =
    let mutable csns = Map.empty
    let mutable state = Sequencing.create ()
    let mutable observations = []

    for action in actions do
        match action with
        | Join ix ->
            // Joining at the current SN, like a live join op does.
            state <- clientJoin state (clientName ix) state.SequenceNumber
            observations <- (state, None) :: observations
        | Leave ix ->
            state <- clientLeave state (clientName ix)
            observations <- (state, None) :: observations
        | Submit(ix, rsnDelta) ->
            let name = clientName ix
            let csn = (Map.tryFind name csns |> Option.defaultValue 0L) + 1L
            let rsn = max 0L (state.SequenceNumber - int64 rsnDelta)

            match assignSequenceNumber state name csn rsn with
            | SequenceOk(next, sn, _) ->
                csns <- Map.add name csn csns
                state <- next
                observations <- (state, Some sn) :: observations
            | SequenceError _ -> observations <- (state, None) :: observations
        | Noop(ix, rsnDelta) ->
            let name = clientName ix
            let rsn = max 0L (state.SequenceNumber - int64 rsnDelta)

            match updateClientRsn state name rsn with
            | Ok next ->
                state <- next
                observations <- (state, None) :: observations
            | Error _ -> observations <- (state, None) :: observations

    List.rev observations

[<Tests>]
let properties =
    testList
        "Sequencing properties"
        [
            testPropertyWithConfig config "assigned SNs are strictly increasing"
            <| fun (actions: Action list) ->
                let sns = replay actions |> List.choose snd
                List.pairwise sns |> List.forall (fun (a, b) -> b > a)

            testPropertyWithConfig config "MSN never decreases across any interleaving"
            <| fun (actions: Action list) ->
                let msns = replay actions |> List.map (fun (s, _) -> s.MinimumSequenceNumber)
                List.pairwise msns |> List.forall (fun (a, b) -> b >= a)

            testPropertyWithConfig config "MSN never exceeds SN"
            <| fun (actions: Action list) ->
                replay actions
                |> List.forall (fun (s, _) -> s.MinimumSequenceNumber <= s.SequenceNumber)

            testCase "validation order is UnknownClient -> InvalidCsn -> InvalidRsn"
            <| fun () ->
                // A submission that violates all three must report UnknownClient.
                let empty = Sequencing.create ()

                match assignSequenceNumber empty "ghost" 0L 99L with
                | SequenceError(UnknownClient _) -> ()
                | other -> failtest $"expected UnknownClient first, got {other}"

                // Known client, bad CSN and bad RSN: CSN wins.
                let joined = clientJoin empty "c" 0L

                let afterOp =
                    match assignSequenceNumber joined "c" 1L 0L with
                    | SequenceOk(s, _, _) -> s
                    | other -> failwith $"setup failed: {other}"

                match assignSequenceNumber afterOp "c" 1L 99L with
                | SequenceError(InvalidCsn _) -> ()
                | other -> failtest $"expected InvalidCsn before InvalidRsn, got {other}"
        ]
