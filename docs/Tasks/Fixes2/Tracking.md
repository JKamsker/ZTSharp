# Fixes2 - Fix -> test mapping

Tick an item once the corresponding test exists and passes in `dotnet test -c Release`.

## Phase 1 - User-space TCP

- [x] 1.1 FIN dispose sends EOF: new tests in `ZTSharp.Tests` (UserSpaceTcp close/FIN lifecycle)
- [x] 1.2 Lost final ACK handshake recovery: new tests in `ZTSharp.Tests` (handshake retransmit)
- [x] 1.3 Dispose vs inbound data race: new tests in `ZTSharp.Tests` (receiver/receive-loop resilience)
- [x] 1.4 Half-close semantics: new tests in `ZTSharp.Tests` (FIN + write-after-FIN behavior)
- [x] 1.5 Remote window wait cannot hang: new tests in `ZTSharp.Tests` (window=0 + close race)
- [x] 1.6 Listener accept queue bounded: new tests in `ZTSharp.Tests` (connection flood + accept backlog)
- [x] 1.7 Routed link drop policy: new tests in `ZTSharp.Tests` (drop telemetry / TCP progress)

## Phase 2 - Dataplane + IP layer

- [x] 2.1 IPv4 header checksum policy enforced: new tests in `ZTSharp.Tests`
- [x] 2.1 UDP checksum validation: new tests in `ZTSharp.Tests` (invalid checksum dropped)
- [x] 2.1 TCP checksum validation: new tests in `ZTSharp.Tests` (invalid checksum SYN dropped)
- [x] 2.1 ICMPv6 checksum validation: new tests in `ZTSharp.Tests` (invalid NS dropped)
- [x] 2.1 TCP encode oversized payload guarded: new tests in `ZTSharp.Tests`
- [x] 2.2 Fragmentation/exthdr policy: new tests in `ZTSharp.Tests` (fragment/exthdr packets handled per policy)
- [x] 2.3 IP->NodeId cache bounded + not poisonable: new tests in `ZTSharp.Tests`
- [x] 2.4 ResolveNodeId TTL/validation: new tests in `ZTSharp.Tests`
- [x] 2.5 Root endpoint filtering policy: new tests in `ZTSharp.Tests`
- [x] 2.6 Peer key negative-cache race: new tests in `ZTSharp.Tests`
- [x] 2.7 Dispatcher loop shutdown correctness (`ChannelClosedException`): new tests in `ZTSharp.Tests`
- [x] 2.8 Hello root correlation: new tests in `ZTSharp.Tests`
- [x] 2.8 Netconf chunk overlap/DoS resistance: new tests in `ZTSharp.Tests`
- [x] 2.8 Netconf signature policy (legacy unsigned configs): new tests in `ZTSharp.Tests`
- [x] 2.8 WHOIS OK robustness to malformed trailing identities: new tests in `ZTSharp.Tests`
- [x] 2.8 Planet/world forward-compat behavior: new tests in `ZTSharp.Tests`
- [x] 2.8 Inline COM strictness/truncation visibility: new tests in `ZTSharp.Tests`
- [x] 2.9 Crypto size caps + allocation hardening: new tests in `ZTSharp.Tests` (and/or `ZTSharp.Benchmarks`)
- [x] 2.5 Direct-endpoint selection ordering/filtering: new tests in `ZTSharp.Tests`
- [x] 2.9 HELLO flood CPU/alloc bounded: new tests in `ZTSharp.Tests`
- [x] 2.9 Compression invalid payload allocation bounded: new tests in `ZTSharp.Tests`
- [x] 2.10 Control-plane packets not silently dropped: new tests in `ZTSharp.Tests`
- [x] 2.10 Drop telemetry surfaced: tests/validation in `ZTSharp.Tests`

## Phase 3 - Socket surface + lifecycle

- [x] 3.1 Any/IPv6Any binds behave as documented: new tests in `ZTSharp.Tests`
- [x] 3.1 Connect with local Any/IPv6Any chooses managed IP: new tests in `ZTSharp.Tests`
- [x] 3.1 IPv6 ScopeId normalization policy enforced: new tests in `ZTSharp.Tests`
- [x] 3.2 Accepted `RemoteEndPoint` populated (or documented): new tests in `ZTSharp.Tests`
- [x] 3.3 Connect/dispose cannot wedge: new tests in `ZTSharp.Tests`
- [x] 3.3 `ZeroTierSocket.DisposeAsync` cannot wedge behind join/runtime: new tests in `ZTSharp.Tests`
- [x] 3.4 `ZeroTierUdpSocket.DisposeAsync` idempotent: new tests in `ZTSharp.Tests`
- [x] 3.5 Timeout helper preserves correct exceptions: new tests in `ZTSharp.Tests`
- [x] 3.6 AES dearmor failure doesn't mutate plaintext (or is documented): new tests in `ZTSharp.Tests`
- [x] 3.7 ManagedSocket `Shutdown` semantics documented/enforced: new tests in `ZTSharp.Tests`
- [x] 3.7 Backlog + port-0 listen policy enforced: new tests in `ZTSharp.Tests`
- [x] 3.8 Real-stack HTTP per-address connect behavior bounded: new tests in `ZTSharp.Tests`

## Phase 4 - Persistence + filesystem

- [x] 4.1 Symlink/junction escape prevented: new tests in `ZTSharp.Tests` (Windows + Unix where possible)
- [x] 4.2 planet/roots delete removes both: new tests in `ZTSharp.Tests`
- [x] 4.3 State-file size caps + streaming reads: new tests in `ZTSharp.Tests`
- [x] 4.4 AtomicFile failures surface: new tests in `ZTSharp.Tests` (Windows-focused)
- [x] 4.5 StateRootPath normalization policy enforced: new tests in `ZTSharp.Tests`
- [x] 4.5 Secret identity file permission/ACL policy documented/enforced: tests/notes as feasible
- [x] 4.6 Key normalization edge cases (invalid chars/reserved names): new tests in `ZTSharp.Tests`

## Phase 5 - Transport + platform

- [x] 5.1 Wildcard local endpoint not rewritten to loopback: new tests in `ZTSharp.Tests`
- [ ] 5.2 IPv4-mapped IPv6 canonicalization: new tests in `ZTSharp.Tests`
- [ ] 5.3 OS UDP receive loop survives socket exceptions: new tests in `ZTSharp.Tests`
- [ ] 5.4 Windows IOCTL behavior verified: Windows-only tests or diagnostics
- [ ] 5.5 Dual-mode bind fallback resilience (IPv6-only before IPv4): new tests in `ZTSharp.Tests`
- [ ] 5.3 Discovery replies don't block receive loop: new tests in `ZTSharp.Tests`
- [ ] 5.6 OS-UDP peer registry bounded (TTL/eviction): new tests in `ZTSharp.Tests`

## Phase 6 - Overlay stack

- [x] 6.1 Channel writer concurrency correctness: new tests in `ZTSharp.Tests`
- [x] 6.2 No silent-drop hangs for overlay HTTP: new tests in `ZTSharp.Tests`
- [x] 6.3 `HttpResponseMessage.Dispose()` never throws/hangs due to overlay stream: new tests in `ZTSharp.Tests`
- [x] 6.3 Overlay connect failures wrapped as `HttpRequestException`: new tests in `ZTSharp.Tests`
- [x] 6.3 Overlay local-port collisions handled: new tests in `ZTSharp.Tests`
- [x] 6.4 Peer discovery framing collision: new tests in `ZTSharp.Tests`
- [x] 6.5 Node lifecycle event re-entrancy deadlock prevented: new tests in `ZTSharp.Tests`
- [x] 6.5 Node dispose cannot wedge indefinitely: new tests in `ZTSharp.Tests`
- [x] 6.5 User event handler exceptions isolated: new tests in `ZTSharp.Tests`
- [x] 6.6 EventLoop not poisoned by callback throw: new tests in `ZTSharp.Tests`
- [x] 6.7 LeaveNetwork ordering avoids transport subscription leak: new tests in `ZTSharp.Tests`
- [x] 6.8 InMemory transport cancellation token doesn't break fanout: new tests in `ZTSharp.Tests`
- [x] 6.9 ActiveTaskSet shutdown semantics: new tests in `ZTSharp.Tests`
- [x] 6.10 Overlay background tasks observed (no unobserved exceptions): new tests in `ZTSharp.Tests`
- [x] 6.10 Overlay dispose bounded (no hang): new tests in `ZTSharp.Tests`
- [x] 6.11 Overlay spoofing rejected (if secured): new tests in `ZTSharp.Tests`

## Phase 7 - Docs + test stability

- [ ] Docs updated: `docs/COMPATIBILITY.md`, `docs/ZEROTIER_STACK.md`, `docs/ZEROTIER_SOCKETS.md`
- [ ] Flaky delays replaced with deterministic waits: `ZTSharp.Tests/**`
