# libzt-dotnet implementation plan

## Status legend
- `[ ]` Pending
- `[x]` Completed

This file tracks the full implementation of a fully managed .NET 10 replacement for `libzt` (no P/Invoke, no native daemon/binaries).

## Milestone M0 — Repository scaffolding and API surface
- [x] Record full implementation strategy and task plan in versioned docs.
- [x] Create repository solution and projects (`JKamsker.LibZt`, tests).
- [x] Add root API namespace and public types (`ZtNode`, `ZtNodeOptions`, events, logging contracts).
- [x] Add pluggable store interfaces and file/memory implementations.
- [x] Add dependency constraints and package references (`.NET 10`, optional crypto and logging libraries).
- [x] Add CI/lint/test project layout with baseline test fixtures.

## Milestone M1 — Core foundation
- [x] Implement deterministic identity/model types (Node ID generation and persistence).
- [x] Implement state serialization/deserialization for identity/peers/networks roots files.
- [x] Add node lifecycle state machine (`Stopped`, `Starting`, `Running`, `Stopping`, `Failed`).
- [ ] Implement in-memory event loop and scheduling primitives.
- [x] Add tests for state store round-trip and identity determinism.

## Milestone M2 — Networking core without OS PHY
- [x] Implement in-memory transport bus for deterministic integration tests.
- [x] Add node-to-node control plane messaging with mock frame exchange.
- [x] Add `Join`, `Leave`, `GetNetworks`, and event dispatch plumbing.
- [x] Implement minimal transport-independent forwarding behavior.
- [x] Add offline integration tests for join/leave and network membership transitions.

## Milestone M3 — Managed user-space stack MVP
	- [x] Implement basic UDP path over managed stack.
	- [x] Implement managed TCP stream/listener primitives using async socket-like abstractions.
	- [ ] Add IPv4 and IPv6 endpoint/address model support.
	- [x] Add `ZtUdpClient`, `ZtTcpClient`, `ZtTcpListener` public APIs.
	- [x] Add offline echo tests for both TCP and UDP traffic flows.

## Milestone M4 — Real interop via `ztnet` validation network
- [ ] Add OS UDP transport adapter and packet framing.
- [ ] Implement node-to-node handshake over real UDP path.
- [ ] Add peer discovery + virtual interface plumbing.
- [x] Validate network creation via local `ztnet` CLI.
- [ ] Validate joining an actual ZeroTier network using local `ztnet` CLI.
- [ ] Verify end-to-end socket communication with external ZeroTier endpoints.

## Milestone M5 — Cross-platform and hardening
- [ ] Add Windows transport first, then Linux/macOS adapters.
- [ ] Add persistence migration/compatibility notes for `planet`/`roots` storage.
- [ ] Add resilience and cancellation semantics.
- [ ] Add performance/memory benchmarks for core loops.
- [ ] Add docs, samples, and API usage guide.
- [ ] Add `AOT` compatibility pass where feasible.

## Ongoing
- [ ] Reconcile licensing constraints from dependent modules (BSD-like sections and change dates).
- [ ] Track any protocol-level compatibility gaps against upstream `libzt`.
