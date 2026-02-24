# Parity1 — Managed libzt parity (biggest missing pieces)

Goal: Make the **managed-only** ZeroTier stack as broadly usable as `libzt` *for apps*, without requiring any OS ZeroTier install and without shipping native binaries.

In this Parity1 pass, the biggest gaps to close are:
- UDP support (user-space sockets)
- IPv6 support (including OS-client interoperability)
- More complete TCP behavior (robustness/perf)
- A more drop-in socket-like API (so apps don’t need to be rewritten around `HttpClient` only)

Non-goals (tracked in `docs/Tasks/Future-Parity-Notes.md`):
- Full ZeroTierOne node parity (all verbs/features/perf knobs)
- Creating an OS-visible network adapter (TUN/TAP/WFP/NPCAP/etc.)

Status legend:
- `[ ]` Pending
- `[x]` Completed (implemented + validated + committed)

## Milestone P0 — Planning + acceptance criteria
- [x] Create this Parity1 task list.
- [x] Create `docs/Tasks/Future-Parity-Notes.md` and capture out-of-scope parity gaps.
- [ ] Define Parity1 acceptance tests (commands + expected results) for:
  - UDP IPv4
  - UDP IPv6
  - TCP stress (many conns + larger payloads)
  - “drop-in” socket API smoke tests

## Milestone P1 — UDP (IPv4) user-space sockets
- [ ] Add UDP codec (header + pseudo-header checksum) and tests.
- [ ] Extend the routed IP demux to handle UDP alongside TCP.
- [ ] Implement a public managed API for UDP:
  - `ZtZeroTierSocket.BindUdpAsync(...)`
  - `SendToAsync(...)` / `ReceiveFromAsync(...)`
  - cancellation + timeouts + disposal semantics
- [ ] Implement basic UDP “port in use” and binding validation.
- [ ] Add a CLI command for UDP (for manual verification + CI-friendly smoke):
  - `libzt udp-listen <port> --stack managed ...`
  - `libzt udp-send --to <ip:port> --data <...> ...`
- [ ] E2E manual verification (OS ZeroTier client -> managed UDP and managed -> OS):
  - OS: `echo -n ping | nc -u -w1 10.121.15.82 9999`
  - managed logs + replies (`pong`)

## Milestone P2 — IPv6 dataplane (frames + address resolution)
- [ ] Ensure we persist/print assigned IPv6 managed IPs (already parsed, but verify state + CLI output).
- [ ] Handle IPv6 in `FRAME`/`EXT_FRAME` (EtherType `0x86DD`) and route to an IPv6 handler.
- [ ] Implement minimal IPv6 parsing/serialization utilities + tests.
- [ ] Implement ICMPv6 Neighbor Discovery (NDP) responder so OS ZeroTier clients can reach us by IPv6:
  - respond to Neighbor Solicitation for our managed IPv6
  - send Neighbor Advertisement (correct flags/target LL address)
- [ ] Subscribe/respond to the relevant multicast groups for IPv6 neighbor discovery (ZeroTier multicast groups / L2 multicast).
- [ ] E2E manual verification:
  - OS: `ping6 <managed-ipv6>` succeeds
  - OS: `curl -g "http://[<managed-ipv6>]:5380/"` hits the managed listener

## Milestone P3 — IPv6 sockets (TCP + UDP)
- [ ] Add IPv6 support to the user-space TCP stack (active-open + passive-open).
- [ ] Add UDP-over-IPv6 support (SendTo/ReceiveFrom).
- [ ] Add E2E tests for IPv6 TCP and UDP (gated by env vars like the existing E2E tests).

## Milestone P4 — TCP robustness (toward libzt expectations)
- [ ] Implement out-of-order segment handling + reassembly.
- [ ] Implement receive-window / flow control + backpressure (avoid unbounded buffering).
- [ ] Improve retransmission behavior (RTO/backoff) and loss recovery for real internet paths.
- [ ] Improve close semantics (FIN/half-close/TIME_WAIT-ish behavior) and error mapping (RST).
- [ ] Add stress tests (many concurrent conns, larger payloads, slow reader/writer).
- [ ] Manual verification: sustained HTTP download over managed stack without stalls/timeouts.

## Milestone P5 — Socket-like managed API (drop-in ergonomics)
- [ ] Define a minimal but practical socket API surface that matches common app usage:
  - TCP connect/listen/accept returning `Stream`
  - UDP datagrams with `ReceiveFromAsync`/`SendToAsync`
  - explicit local bind + cancellation + timeouts
- [ ] Add a compatibility wrapper layer to ease porting from `System.Net.Sockets.Socket` / `ZeroTier.Sockets.Socket`-style code.
- [ ] Add samples showing:
  - TCP echo server/client
  - UDP request/response
  - `HttpClient` over managed TCP (already) + `SocketsHttpHandler.ConnectCallback` example
- [ ] Document supported subset + known differences vs OS sockets.

