# Fully managed real ZeroTier stack (MVP)

Goal: join an existing controller-based ZeroTier network (normal NWIDs like `9ad07d01093a69e3`) without an OS ZeroTier install and without native binaries, then open outbound TCP connections by managed IP (e.g. `10.121.15.99:5380`) via `HttpClient`.

Status legend:
- `[ ]` Pending
- `[x]` Completed (implemented + validated + committed)

## Milestone Z0 — Repo/task scaffolding
- [x] Add initial task breakdown (this file).

## Milestone Z1 — Public API + CLI wiring (scaffolding, no networking yet)
- [x] Add `JKamsker.LibZt.ZeroTier` public API stubs (`ZtZeroTierSocketOptions`, `ZtZeroTierSocket`).
- [x] Add `ZtZeroTierHttpMessageHandler` stub that plugs into `HttpClient`.
- [x] Update CLI: accept `--stack zerotier` and `--stack libzt` (alias), route `call` through new API.
- [x] Add docs: `docs/ZEROTIER_STACK.md` with intended usage and current limitations.
- [x] Add minimal unit tests for API surface (compiles, basic argument validation).

## Milestone Z2 — ZeroTier-compatible identity (must interop with real networks)
- [x] Implement ZeroTier identity generation (address + key material) in managed code.
- [x] Persist identity under `--state` in a new managed format (`<state>/zerotier/...`).
- [x] Add tests with fixed vectors (derived from upstream docs) for node id derivation.

## Milestone Z3 — UDP transport + packet framing
- [x] Implement a UDP transport loop (send/recv, cancellation, timeouts, logging).
- [x] Implement packet encode/decode scaffolding (enough to start parsing control packets).
- [x] Add unit tests for codec roundtrips.

## Milestone Z4 — Network join (controller-based NWID)
- [x] Implement bootstrap from planet/roots (configurable planet source).
  - [x] Embed default planet bytes (no network fetch).
  - [x] Implement `World` (planet) binary decode (roots + stable endpoints).
  - [x] Add unit tests for planet decode (roots present, endpoints valid).
- [ ] Implement secure session establishment and controller config fetch.
  - [x] Implement identity wire serialization (Identity::serialize format).
  - [x] Implement HELLO send + OK parse (unencrypted) over UDP.
  - [x] Implement C25519 key agreement + packet armor/dearmor for non-HELLO.
  - [ ] Implement NETWORK_CONFIG_REQUEST flow and parse responses.
- [ ] Persist assigned managed IPs to state and expose them via API.
  - [ ] Persist network config + assigned IPs under `<state>/zerotier/`.
  - [ ] Expose assigned IPs via `ZtZeroTierSocket.ManagedIps`.

## Milestone Z5 — Outbound TCP + HttpClient “just works”
- [ ] Implement minimal user-space IPv4 + ARP + TCP active-open (client only).
  - [ ] Add minimal IPv4 stack (routing + ICMP optional).
  - [ ] Add TCP active-open (client) with streams.
- [ ] Wire `ZtZeroTierHttpMessageHandler` to dial `http://<zt-ip>:<port>` via user-space TCP.
  - [ ] Resolve ZeroTier managed IPs to overlay peers using network config/ARP.
  - [ ] Implement `ConnectTcpAsync` to return a stream backed by user-space TCP.
- [ ] Add opt-in E2E test (`LIBZT_RUN_ZEROTIER_E2E`) with env-configured NWID + URL.
