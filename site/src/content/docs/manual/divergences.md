---
title: "Divergence notes"
description: Every deliberate departure from the reference servers, with its reasoning.
---

Undertow departs from its references only on purpose, and every departure is
written down — here and in `tests/fixtures/wire/README.md`. Silent divergence
is treated as a bug.

## IClient echo is verbatim, not key-sorted

The Gleam fixtures show the client's IClient re-serialized with term-sorted
keys — an Erlang map cannot preserve insertion order. Undertow preserves the
supplied key order instead.

This matters because the Fluid container loader seeds its own audience entry
with the object it *sent* (original key order), and its assert `0x4b2`
requires byte-identity with any later add for the same client id. In live
browser flows the Gleam reference's re-sorting tripped exactly this: the
loader received its own presence join back in a different key order and
closed the container (todo-list e2e: 6/9 before, 9/9 after).

The shared fix, landed in **both** servers, pins `initialSignals` in
IConnected to `[]` — matching Levee — so the loader never receives its own
join back through a byte-reordering path. Undertow keeps the verbatim echo
anyway: every remaining echo path (join op `data`, `initialClients`, peer
presence signals) then matches the client's own bytes too, which a sort can
never do. The wire differ flags the key order inside those payloads as the
one expected difference against the recorded fixtures.

## Phoenix bad-vsn rejection is 403

A Phoenix join with `vsn=1.0.0` is rejected **403 before the WebSocket
upgrade**. An earlier plan document assumed 400; the captured fixture
(`phoenix-bad-vsn.txt`) pins 403, and the fixture wins.

## Drop-in parity: one intentional 401

Against the Levee integration suite, Undertow passes 53 of 54 cases. The
remaining case intentionally returns **401**, recorded as **ADR-009** — a
deliberate authorization-behavior decision, not an accident of porting.

## Baseline skips

The Routerlicious conformance baseline includes 3 skipped tests and 1 todo.
These are the reference suite's own state at capture time — Floodgate posts
the same counts.
