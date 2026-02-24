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
- [x] Implement secure session establishment and controller config fetch.
  - [x] Implement identity wire serialization (Identity::serialize format).
  - [x] Implement HELLO send + OK parse (unencrypted) over UDP.
  - [x] Implement C25519 key agreement + packet armor/dearmor for non-HELLO.
  - [x] Implement NETWORK_CONFIG_REQUEST flow and parse responses.
- [x] Persist assigned managed IPs to state and expose them via API.
  - [x] Persist network config + assigned IPs under `<state>/zerotier/`.
  - [x] Expose assigned IPs via `ZtZeroTierSocket.ManagedIps`.

## Milestone Z5 — Outbound TCP + HttpClient “just works”
- [x] Implement root-relayed dataplane (single-root MVP).
  - [x] Implement managed `MAC` + `MulticastGroup` primitives (address resolution groups).
  - [x] Resolve ZeroTier managed IPs to node ids via `MULTICAST_GATHER`.
  - [x] Implement `WHOIS` peer identity cache + C25519 shared keys.
  - [x] Implement `FRAME`/`EXT_FRAME` TX/RX for IPv4 payloads (include inline COM for MVP).
- [x] Implement minimal user-space IPv4 + TCP active-open (client only).
  - [x] Add IPv4 codec + checksum helpers.
  - [x] Add TCP codec + MSS option (small MSS to avoid ZT fragmentation).
  - [x] Add TCP active-open (client) with a `Stream` abstraction (basic retransmit).
- [x] Wire `ZtZeroTierHttpMessageHandler` to dial `http://<zt-ip>:<port>` via user-space TCP.
  - [x] Implement `ConnectTcpAsync` to return a stream backed by user-space TCP.
- [x] Add opt-in E2E test (`LIBZT_RUN_ZEROTIER_E2E`) with env-configured NWID + URL.

## Milestone Z6 — CLI + docs alignment
- [x] Make CLI `--stack managed` use the real ZeroTier managed stack; add `--stack overlay` for the legacy managed overlay stack (keep `zerotier`/`libzt` as aliases for `managed`).
- [x] Update tunnel demo + ztnet scripts/docs to pass `--stack overlay` where they rely on the legacy overlay stack.
- [x] Implement `join --stack managed` (one-shot join + print node id + assigned managed IPs).
- [x] Update docs (`README.md`, `docs/USAGE.md`, `docs/COMPATIBILITY.md`, `docs/E2E.md`, `docs/ZEROTIER_STACK.md`) to reflect the real ZeroTier stack MVP.

## Milestone Z7 — Robustness fixes
- [x] Ignore unreachable root endpoints during HELLO root discovery (e.g. when IPv6 has no route).
- [x] Send `HELLO` to the controller before `NETWORK_CONFIG_REQUEST` (controller must learn the node identity before decrypting).
- [x] Include request metadata dictionary in `NETWORK_CONFIG_REQUEST` (version/protocol/rules-engine).
- [x] Surface `ERROR(NETWORK_CONFIG_REQUEST)` as a meaningful exception (e.g. not authorized) instead of timing out.
- [x] Print node id before join errors in the CLI (so the node can be authorized).

## Milestone Z8 — libzt parity for “dial by managed IP”
- [x] Import existing `libzt` state identity (`<state>/libzt/identity.secret`) into the managed identity store when present.
- [x] Prefer a planet/roots file from state when present (`<state>/libzt/roots`, `<state>/planet`) and fall back to the embedded planet.
- [x] Include inline COM in `MULTICAST_GATHER` requests and surface `ERROR(MULTICAST_GATHER)` as a meaningful exception.
- [x] Send `HELLO` to the remote peer before starting `EXT_FRAME` TCP traffic (introduce our identity).
- [x] Retransmit TCP SYN during `ConnectAsync` (avoid single-shot SYN and brittle 10s connect wait).
- [x] Cache upstream root (HELLO OK + root key) from join and reuse for dials (avoid extra root discovery and timeouts).
- [x] Surface data-plane `ERROR(...)` packets and propagate link failures into TCP connect/receive (avoid silent timeouts).
- [x] Add/extend unit tests for the above.

## Milestone Z9 — NAT traversal (RENDEZVOUS) for `EXT_FRAME`
- [x] Implement `RENDEZVOUS` payload codec + unit tests.
- [x] Handle upstream root control packets (`RENDEZVOUS`, `ERROR`) in `ZtZeroTierIpv4Link` using the root key.
- [x] Handle peer `PUSH_DIRECT_PATHS` control packets and track direct endpoints for the session.
- [x] Use direct endpoint hints to send UDP hole-punch and prefer direct peer endpoints for `EXT_FRAME`.

## Milestone Z10 — Address resolution (ARP/multicast) for inbound traffic
- [x] Implement `MULTICAST_LIKE` and subscribe to our address-resolution group (so other peers can ARP for our managed IP).
- [x] Parse `MULTICAST_FRAME` and respond to inbound ARP requests for our managed IP.
- [x] Manual verification: `libzt call --stack managed --network 9ad07d01093a69e3 --url http://10.121.15.99:5380/` returns an HTTP response (after network authorization).

## Milestone Z11 — Listening sockets (TCP passive-open)
- [x] Add a long-lived managed dataplane runtime (single UDP socket) that can RX/TX `EXT_FRAME` for multiple peers.
- [x] Implement TCP passive-open (listen/accept) on top of the user-space IPv4/TCP stack.
- [x] Expose a public API (`ZtZeroTierSocket.ListenTcpAsync` + `AcceptAsync`) that returns a `Stream`.
- [x] Update CLI `expose` to support `--stack managed` using the new listener.
- [x] Manual verification: start `libzt expose --stack managed` and connect from another ZeroTier network participant to `http://<managed-ip>:<port>/`.

## Milestone Z12 — CLI listen logger (managed-only)
- [x] Add `libzt listen <port> --stack managed` that joins the network and listens for inbound TCP connections.
- [x] Log HTTP request line + headers for each accepted connection and respond `HTTP 200 OK`.

## Milestone Z13 — OS ZeroTier client can connect inbound
- [ ] Add trace logs in `ZtZeroTierDataplaneRuntime` for first inbound packets (`LIBZT_ZEROTIER_TRACE=true`).
- [ ] Handle peer `HELLO` and respond with `OK(HELLO)` in `ZtZeroTierDataplaneRuntime`.
- [ ] Handle `RENDEZVOUS` (from root) and `PUSH_DIRECT_PATHS` (from peers) in `ZtZeroTierDataplaneRuntime` and send UDP hole punches.
- [ ] Manual verification: from an OS ZeroTier client on `9ad07d01093a69e3`, `curl http://10.121.15.82:5380/` receives `ok`.
