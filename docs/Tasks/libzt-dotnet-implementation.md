# libzt-dotnet implementation plan

## Status legend
- `[ ]` Pending
- `[x]` Completed

This file tracks the full implementation of a fully managed .NET 10 replacement for `libzt` (no P/Invoke, no native daemon/binaries).

## Milestone M0 — Repository scaffolding and API surface
- [x] Record full implementation strategy and task plan in versioned docs.
- [x] Create repository solution and projects (`JKamsker.LibZt`, tests).
- [x] Add root API namespace and public types (`Node`, `NodeOptions`, events, logging contracts).
- [x] Add pluggable store interfaces and file/memory implementations.
- [x] Add dependency constraints and package references (`.NET 10`, optional crypto and logging libraries).
- [x] Add test project layout with baseline test fixtures.
- [x] Add CI pipeline to run `dotnet test -c Release`.
- [x] Add formatting/linting config and wire into CI.

## Milestone M1 — Core foundation
- [x] Implement deterministic identity/model types (Node ID generation and persistence).
- [x] Implement state serialization/deserialization for identity and joined networks.
- [x] Add `planet` / `roots` state store alias handling.
- [x] Add persisted peer directory/state (if required for future discovery/routing/controller semantics).
- [x] Add node lifecycle state machine (`Created`, `Starting`, `Running`, `Stopping`, `Stopped`, `Faulted`).
- [x] Implement event stream and lifecycle/network event emission.
- [x] Implement in-memory event loop and scheduling primitives (timers, work queue) for future protocol state machines.
- [x] Add tests for state store round-trip and identity determinism.

## Milestone M2 — Networking core without OS PHY
- [x] Implement in-memory transport bus for deterministic integration tests.
- [x] Add node-to-node frame exchange with mock transport.
- [x] Add `Join`, `Leave`, `GetNetworks`, and event dispatch plumbing.
- [x] Implement minimal transport-independent forwarding behavior.
- [x] Add offline integration tests for join/leave and network membership transitions.

## Milestone M3 — Managed user-space stack MVP
	- [x] Implement UDP-like datagrams over node transport (`UdpClient`).
	- [x] Implement overlay TCP stream/listener primitives over node transport.
	- [x] Provide OS TCP wrapper APIs for local tests (`TcpClient`, `TcpListener`).
	- [x] Add IPv4/IPv6 overlay endpoint/address model support (virtual NIC / lwIP parity).
	- [x] Add IPv4/IPv6 support in `OsUdp` transport.
	- [x] Add `UdpClient`, `TcpClient`, `TcpListener` public APIs.
	- [x] Add offline echo tests for UDP frames and OS TCP loopback.

## Milestone M4 — Real interop via `net` validation network
- [x] Add OS UDP transport adapter and packet framing.
- [x] Implement node-to-node handshake over real UDP path.
- [x] Add peer endpoint registration API and OS UDP peer mapping.
- [x] Add in-process peer discovery handshake over OS UDP (HELLO control frames).
- [x] Add virtual network interface plumbing (in-managed interface + raw packet framing; OS TUN/TAP parity remains future/native).
- [x] Validate network creation via local `net` CLI.
- [x] Run manual `net network create` command with configured local credentials.
- [x] Validate joining a `net`-managed network (membership + authorization) using local `net` CLI (does not join the ZeroTier overlay yet).
- [x] Verify end-to-end socket communication between managed nodes in that network (`UdpClient` + `OverlayTcpClient`).
- [x] Add optional E2E test scaffold gated by `LIBZT_RUN_E2E` for external CLI smoke checks.

## Milestone M5 — Cross-platform and hardening
- [x] Add OS UDP transport with cross-platform support (Windows/Linux/macOS via `UdpClient`, IPv4 fallback when IPv6 is unavailable).
- [x] Add persistence migration/compatibility notes for `planet`/`roots` storage.
- [x] Add resilience and cancellation semantics.
- [x] Add performance/memory benchmarks for core loops.
- [x] Add docs, samples, and API usage guide.
- [x] Add `AOT` compatibility pass where feasible.

## Ongoing
- [x] Reconcile licensing constraints from dependent modules (BSD-like sections and change dates).
- [x] Track any protocol-level compatibility gaps against upstream `libzt`.

## Milestone M6 — Memory-first API modernization
- [x] Replace public `byte[]` payload/identity return surfaces with `ReadOnlyMemory<byte>` or `ReadOnlySpan<byte>` where practical.
- [x] Remove frame copy on OS-UDP forwarding path (`ToArray`-based handoff).
- [x] Audit and remove remaining `byte[]`-based framing/list-materialization allocations in internal transport and store hot paths.
- [x] Add allocation benchmarks for hot packet and dispatch paths.
- [x] Replace manual `new byte[...]` in transport hot paths with `ArrayPool`/`Span` strategy where safe and measurable.
