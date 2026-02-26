# Real ZeroTier stack — peer path negotiation + NAT traversal + multipath bonding

Goal: implement full peer path negotiation / NAT traversal optimization for the **real ZeroTier stack** (`ZTSharp.ZeroTier`),
including ZeroTierOne-style multipath bonding (QoS measurement + path negotiation), while keeping the feature **off by default**
to avoid behavior changes for existing users.

Status legend:
- Pending: `- [ ]`
- Completed (implemented + validated): `- [x]`

## P0 — Task breakdown + guardrails
- [ ] Add `ZeroTierSocketOptions` multipath/bonding configuration surface (default disabled).
- [x] Add internal transport abstraction that supports multi-socket receive/send with `LocalSocketId`.
- [x] Thread `LocalSocketId` + remote endpoint through dataplane RX loops into peer processing.

## P1 — Direct path learning + keepalives
- [ ] Track per-peer physical paths `(LocalSocketId, RemoteEndPoint)` for **hops==0** traffic only (learn/refresh/expire).
- [ ] Implement `ECHO` + `OK(ECHO)` for keepalives and latency measurement (rate-limited).
- [ ] Parse `OK(HELLO)` for peer protocol version + latency update and store external surface addresses (self-awareness seed).

## P2 — Direct path sending policy (direct + root fallback)
- [ ] Implement flow-id derivation (stable hash) from IPv4/IPv6 TCP/UDP 5-tuple.
- [ ] Implement per-peer send selection: prefer best direct path(s) with **root fallback** + configurable warm-up duplication.
- [ ] Update `PUSH_DIRECT_PATHS` handling to manage add/forget/redirect hints and trigger probing.

## P3 — QoS measurement + PATH_NEGOTIATION_REQUEST
- [ ] Implement `QOS_MEASUREMENT` RX parsing (little-endian id/holdingTime pairs) and per-path QoS state.
- [ ] Implement `QOS_MEASUREMENT` TX generation and periodic sending cadence.
- [ ] Implement `PATH_NEGOTIATION_REQUEST` RX/TX and utility-based tie-break logic.

## P4 — Multipath bonding policies
- [ ] Implement bond policy engine with: active-backup, broadcast, balance-rr, balance-xor, balance-aware.
- [ ] Implement background maintenance loop (tick) for: heartbeats, QoS sends, flow expiration/rebalance, negotiation checks.

## P5 — CLI/docs/tests
- [ ] Add CLI flags to enable/configure multipath and bond policy (enough for manual verification).
- [ ] Add/extend unit tests for: multi-transport socket id propagation, echo, qos parsing, bond policy selection.
- [x] Stabilize flaky `ZeroTierTcpListenerBacklogTests` timeout under full-suite load.
- [ ] Update docs: `docs/ZEROTIER_STACK.md` and `docs/COMPATIBILITY.md` to reflect new behavior and defaults.
