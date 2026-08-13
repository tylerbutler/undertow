namespace Undertow.Protocol

/// Port of spillway/sequencing: CSN/SN/RSN/MSN management.
///
/// - CSN: per-client, monotonically increasing from 1
/// - SN: server-assigned, globally monotonically increasing
/// - RSN: client's last-seen SN when creating an op
/// - MSN: min(RSN of all connected clients), never decreases
module Sequencing =

    type ClientSequenceState = { LastCsn: int64; LastRsn: int64 }

    type SequenceState =
        {
            SequenceNumber: int64
            MinimumSequenceNumber: int64
            ClientStates: Map<string, ClientSequenceState>
        }

    type SequenceError =
        | InvalidCsn of expectedGreaterThan: int64 * received: int64
        | InvalidRsn of currentSn: int64 * receivedRsn: int64
        | UnknownClient of clientId: string

    type SequenceResult =
        | SequenceOk of state: SequenceState * assignedSn: int64 * msn: int64
        | SequenceError of SequenceError

    let create () =
        {
            SequenceNumber = 0L
            MinimumSequenceNumber = 0L
            ClientStates = Map.empty
        }

    let fromCheckpoint (sn: int64) (msn: int64) =
        {
            SequenceNumber = sn
            MinimumSequenceNumber = msn
            ClientStates = Map.empty
        }

    /// MSN = min(last RSN of all connected clients); with no clients it stays
    /// put, and it can never decrease.
    let private calculateMsn (clientStates: Map<string, ClientSequenceState>) (currentMsn: int64) =
        if Map.isEmpty clientStates then
            currentMsn
        else
            let minRsn = clientStates |> Seq.map (fun kv -> kv.Value.LastRsn) |> Seq.min
            max minRsn currentMsn

    /// Register a client joining; its initial RSN comes from the join.
    let clientJoin (state: SequenceState) (clientId: string) (joinRsn: int64) =
        let clients =
            Map.add clientId { LastCsn = 0L; LastRsn = joinRsn } state.ClientStates

        { state with
            ClientStates = clients
            MinimumSequenceNumber = calculateMsn clients state.MinimumSequenceNumber
        }

    let clientLeave (state: SequenceState) (clientId: string) =
        let clients = Map.remove clientId state.ClientStates

        { state with
            ClientStates = clients
            MinimumSequenceNumber = calculateMsn clients state.MinimumSequenceNumber
        }

    /// Assign a sequence number to an incoming op. Validation order is part of
    /// the contract: UnknownClient -> InvalidCsn -> InvalidRsn.
    let assignSequenceNumber (state: SequenceState) (clientId: string) (csn: int64) (rsn: int64) =
        match Map.tryFind clientId state.ClientStates with
        | None -> SequenceError(UnknownClient clientId)
        | Some clientState when csn <= clientState.LastCsn ->
            SequenceError(InvalidCsn(clientState.LastCsn, csn))
        | Some _ when rsn > state.SequenceNumber ->
            SequenceError(InvalidRsn(state.SequenceNumber, rsn))
        | Some _ ->
            let newSn = state.SequenceNumber + 1L
            let clients = Map.add clientId { LastCsn = csn; LastRsn = rsn } state.ClientStates
            let newMsn = calculateMsn clients state.MinimumSequenceNumber

            let newState =
                {
                    SequenceNumber = newSn
                    MinimumSequenceNumber = newMsn
                    ClientStates = clients
                }

            SequenceOk(newState, newSn, newMsn)

    /// Update a client's RSN without submitting an op (e.g. from a noop).
    /// RSN can only increase.
    let updateClientRsn (state: SequenceState) (clientId: string) (newRsn: int64) =
        match Map.tryFind clientId state.ClientStates with
        | None -> Error(UnknownClient clientId)
        | Some clientState ->
            let updated =
                { clientState with
                    LastRsn = max clientState.LastRsn newRsn
                }

            let clients = Map.add clientId updated state.ClientStates

            Ok
                { state with
                    ClientStates = clients
                    MinimumSequenceNumber = calculateMsn clients state.MinimumSequenceNumber
                }

    /// Reserve a sequence number for a server-minted system message (e.g. a
    /// summaryAck): advances SN by one so the next client op can't collide.
    let reserveSequenceNumber (state: SequenceState) =
        let reservedSn = state.SequenceNumber + 1L
        let newMsn = calculateMsn state.ClientStates state.MinimumSequenceNumber

        { state with
            SequenceNumber = reservedSn
            MinimumSequenceNumber = newMsn
        },
        reservedSn

    let currentSn (state: SequenceState) = state.SequenceNumber
    let currentMsn (state: SequenceState) = state.MinimumSequenceNumber
    let clientCount (state: SequenceState) = Map.count state.ClientStates

    let isClientConnected (state: SequenceState) clientId =
        Map.containsKey clientId state.ClientStates

    /// All connected client IDs (sorted, matching Erlang dict key order).
    let connectedClients (state: SequenceState) =
        state.ClientStates |> Map.toList |> List.map fst
