# Fixes2 - Correctness, resilience, and security hardening (batch 2 findings)

## Maintenance (this file)
- [ ] Normalize formatting (indentation, separators, ASCII punctuation)
- [ ] Keep Phase items checkbox-based (one checkbox per subtask)

## Summary

Implement fixes for all Critical→Medium issues found across both stacks:

- Real ZeroTier managed stack: `ZTSharp.ZeroTier`
- Legacy overlay stack: `ZTSharp`

Primary goals:

- No known deadlocks/wedges on lifecycle or dispose paths
- No local privilege-escalation primitives via persistence/state directories
- Deterministic behavior under load (no silent drops on TCP-like paths without visibility)
- Cross-platform endpoint handling (dual-mode IPv6, IPv4-mapped IPv6 canonicalization)

---

## Phase 0 - Baseline / reproducibility

- [x] Record baseline: `dotnet build -c Release` and `dotnet test -c Release` (OS + date + summary)
  - 2026-02-26 (Windows 10.0.26200, win-x64, .NET SDK 10.0.100): `dotnet build -c Release` OK (0 warnings, 0 errors); `dotnet test -c Release` OK (136 passed, 6 skipped, 0 failed)
- [x] Add a single “Fixes2” tracking checklist mapping each fix → test(s) (`docs/Tasks/Fixes2/Tracking.md`)
- [ ] Add a short “repro harness” note for each critical hang (how to reproduce locally)
- [ ] Verify Fixes2 scope covers all batch-1 + batch-2 findings (no missing bullets vs consolidated list)

---

## Phase 1 - Real stack: user-space TCP correctness (`ZTSharp/ZeroTier/Net/**`)

### 1.1 FIN / close correctness (critical)
- [ ] Repro + add test: disposing client/server should cause peer stream EOF (`ReadAsync` returns `0`) within bounded time (`ZTSharp/ZeroTier/Net/UserSpaceTcpClient.cs`, `ZTSharp/ZeroTier/Net/UserSpaceTcpServerConnection.cs`)
- [ ] Fix: ensure FIN is actually sent on dispose and peer observes close (avoid gating FIN on `RemoteClosed`)
- [ ] Add test: FIN is sent exactly once and no post-close data is delivered

### 1.2 Handshake robustness (critical hang)
- [ ] Repro + add test: drop client’s final ACK after SYN-ACK; ensure server still completes accept (bounded) (`ZTSharp/ZeroTier/Net/UserSpaceTcpServerReceiveLoop.cs`, `ZTSharp/ZeroTier/Net/UserSpaceTcpReceiveLoop.cs`)
- [ ] Fix: add SYN-ACK retransmit timer until handshake completes (server side)
- [ ] Fix: client should ACK SYN-ACK retransmissions even after it considers itself “connected”
- [ ] Add test: SYN-ACK retransmit eventually recovers from a lost final ACK

### 1.3 Dispose/receive-loop race hardening
- [ ] Repro + add test: dispose concurrently with inbound data; no `InvalidOperationException`/`ObjectDisposedException` escapes receive loop (`ZTSharp/ZeroTier/Net/UserSpaceTcpReceiver.cs`, `ZTSharp/ZeroTier/Net/UserSpaceTcpReceiveLoop.cs`)
- [ ] Fix: isolate `PipeWriter.FlushAsync`/writer completion races (catch + treat as shutdown)
- [ ] Add test: no unobserved task exceptions when disposing mid-stream

### 1.4 Half-close semantics (behavioral decision)
- [ ] Decide + document: support half-close (allow writes after peer FIN) vs explicitly not supported
- [ ] If supported: implement “remote closed” state that still allows sending until local close
- [ ] Add test: peer FIN then local send still succeeds (or fails with a documented exception)

### 1.5 Remote send-window wait robustness
- [ ] Repro + add test: race “remote window==0” with remote close/reset; local `WriteAsync` should not hang without cancellation (`ZTSharp/ZeroTier/Net/UserSpaceTcpSender.cs`, `ZTSharp/ZeroTier/Net/UserSpaceTcpRemoteSendWindow.cs`)
- [ ] Fix: ensure close/fail signals are observable to future waiters (not only existing waiters)

### 1.6 Listener accept-queue backpressure
- [ ] Repro + add test: connection flood while app never accepts → memory doesn’t grow unbounded (`ZTSharp/ZeroTier/ZeroTierTcpListener.cs`)
- [ ] Fix: bound accepted-stream queue (bounded channel) and decide policy (drop/close oldest, refuse new, or backpressure handshake)
- [ ] Add tracing/metrics: accepted queue saturation is visible to callers/tests

### 1.7 Routed link drop policy for TCP-like traffic
- [ ] Repro + add test: overload routed link channel; ensure TCP can still make progress or fails predictably (`ZTSharp/ZeroTier/Internal/ZeroTierRoutedIpv4Link.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierRoutedIpv6Link.cs`)
- [ ] Fix: separate control vs data, or avoid `DropOldest` for in-order TCP segments (or increase capacity + add drop telemetry)

---

## Phase 2 - Real stack: dataplane + IP layer correctness (`ZTSharp/ZeroTier/Internal/**`, `ZTSharp/ZeroTier/Net/**`)

### 2.1 Checksum validation on receive (security/correctness)
- [ ] Add test: invalid IPv4 header checksum is dropped (or explicitly ignored/documented) (`ZTSharp/ZeroTier/Net/Ipv4Codec.cs`)
- [ ] Fix: decide and implement IPv4 header checksum policy (validate or explicitly ignore with rationale)
- [ ] Add test: invalid-checksum UDP to registered port is dropped (`ZTSharp/ZeroTier/Net/UdpCodec.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierDataplaneIpHandler.cs`)
- [ ] Fix: verify UDP checksum on receive (IPv4 + IPv6) before delivering to UDP handlers
- [ ] Add test: invalid-checksum TCP SYN is dropped / does not trigger listener dispatch (`ZTSharp/ZeroTier/Net/TcpCodec.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierDataplaneIpHandler.cs`)
- [ ] Fix: use `TcpCodec.TryParseWithChecksum` for TCP receive paths that influence routing/state
- [ ] Add test: invalid-checksum ICMPv6 NS is dropped (`ZTSharp/ZeroTier/Internal/ZeroTierDataplaneIcmpv6Handler.cs`)
- [ ] Fix: validate ICMPv6 checksum for processed message types (Echo/NS/NA)
- [ ] Add test: `TcpCodec.Encode` rejects/guards oversized payloads that would truncate checksum length (`ZTSharp/ZeroTier/Net/TcpCodec.cs`)
- [ ] Fix: enforce payload length bounds for TCP encoding (avoid `ushort` truncation in checksum length)

### 2.2 Fragmentation / extension headers (explicit policy)
- [ ] Decide + document: (a) support IPv4 fragments, (b) drop fragments explicitly, or (c) accept only non-fragmented packets
- [ ] If dropping: implement explicit “drop fragmented” check with trace hook (`ZTSharp/ZeroTier/Net/Ipv4Codec.cs`, `ZTSharp/ZeroTier/Protocol/ZeroTierPacketHeader.cs`)
- [ ] Decide + document: IPv6 extension-header handling (support a minimal subset vs drop)
- [ ] If dropping: implement explicit detection + drop with trace hook (`ZTSharp/ZeroTier/Net/Ipv6Codec.cs`)

### 2.3 IP→NodeId cache poisoning + unbounded growth
- [ ] Repro + add test: spoof many src IPs → cache growth is bounded (`ZTSharp/ZeroTier/Internal/ZeroTierDataplaneIpHandler.cs`)
- [ ] Fix: add capacity limit + eviction policy (and/or TTL) for `_managedIpToNodeId`
- [ ] Fix: restrict learning rules (only from validated neighbor discovery / ARP, or only from specific authenticated/control flows)
- [ ] Add test: spoofed src IP cannot poison resolution for an unrelated managed IP (within test harness constraints)

### 2.4 ResolveNodeId stability (staleness + ambiguity)
- [ ] Add test: multicast-gather returning multiple members does not permanently cache a wrong node (`ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRootClient.cs`)
- [ ] Fix: add TTL for resolved entries and/or validate candidate before caching forever

### 2.5 Root endpoint filtering + direct endpoints coherence
- [ ] Add test: dataplane RX accepts legitimate root responses even if physical endpoint differs (within allowed set) (`ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRxLoops.cs`)
- [ ] Fix: replace strict `RemoteEndPoint == _rootEndpoint` with normalized/allowed endpoint matching (and document policy)
- [ ] Reconcile direct-path behavior vs docs: decide whether direct endpoints are supported, partially supported, or disabled
- [ ] Add tests: direct endpoints are either (a) used for sending/routing as designed, or (b) ignored by design with docs updated (`ZTSharp/ZeroTier/Internal/ZeroTierIpv4LinkSender.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierDirectEndpointManager.cs`)
- [ ] Add unit tests: `ZeroTierDirectEndpointSelection.Normalize` ordering/filtering/dedupe is correct (and excludes relay endpoint) (`ZTSharp/ZeroTier/Internal/ZeroTierDirectEndpointSelection.cs`)
- [ ] Fix: ensure `ZeroTierDirectEndpointSelection.IsPublicAddress` rejects `::`/`IPv6Any` and handles IPv4-mapped IPv6 consistently (`ZTSharp/ZeroTier/Internal/ZeroTierDirectEndpointSelection.cs`)
- [ ] Fix: move hole-punch sends off RX hot path (throttle/dedupe; don’t block dispatcher/peer loops) (`ZTSharp/ZeroTier/Internal/ZeroTierDirectEndpointManager.cs`)

### 2.6 Peer key negative-cache race
- [ ] Add test: HELLO caches positive key; concurrent failing WHOIS must not overwrite with negative cache (`ZTSharp/ZeroTier/Internal/ZeroTierDataplanePeerSecurity.cs`)
- [ ] Fix: prevent negative caching from overriding a non-expired positive key; special-case shutdown cancellation

### 2.7 Dispatcher/peer-loop shutdown correctness
- [ ] Add test: disposing UDP transport/runtime cannot fault dispatcher loop with `ChannelClosedException` (`ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRxLoops.cs`, `ZTSharp/ZeroTier/Transport/ZeroTierUdpTransport.cs`)
- [ ] Fix: handle `ChannelClosedException` in dispatcher loop (treat as shutdown) and ensure runtime dispose swallows expected loop termination

### 2.8 Join + protocol parsing robustness (Hello/Whois/Netconf/Planet)
- [ ] Add test: OK(HELLO) is only accepted if `Header.Source` matches the root that was pinged for the referenced `inRePacketId` (`ZTSharp/ZeroTier/Internal/ZeroTierHelloClient.cs`)
- [ ] Fix: validate OK(HELLO) source/root correlation to prevent root selection mixups
- [ ] Add tests: netconf chunk assembly rejects overlaps/dup-length attacks and fails fast (not timeout) (`ZTSharp/ZeroTier/Internal/ZeroTierNetworkConfigProtocol.cs`)
- [ ] Fix: track received byte ranges (not just `chunkIndex`) and enforce sane bounds (max chunks, max total length)
- [ ] Decide + document: legacy “unsigned config” acceptance policy (allowed vs rejected) (`ZTSharp/ZeroTier/Internal/ZeroTierNetworkConfigParsing.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierNetworkConfigProtocol.cs`)
- [ ] Fix: enforce signature-required policy (or explicitly gate legacy mode behind an option)
- [ ] Add test: WHOIS OK parsing tolerates trailing malformed identity blobs without aborting join (`ZTSharp/ZeroTier/Internal/ZeroTierWhoisClient.cs`)
- [ ] Fix: bound/validate WHOIS OK parsing loop and isolate per-identity parse failures
- [ ] Add test: embedded planet/world decoding fails gracefully with forward-compat signals rather than hard crash (`ZTSharp/ZeroTier/Protocol/ZeroTierWorldCodec.cs`)
- [ ] Fix: revisit hard caps (roots/endpoints) and decide forward-compat story (bump caps vs “fail closed” with actionable error)
- [ ] Add test: inline COM parsing rejects/flags unexpected trailing bytes (or explicitly documents truncation) (`ZTSharp/ZeroTier/Internal/ZeroTierInlineCom.cs`)
- [ ] Fix: enforce strict length (or make truncation observable via trace/log)

### 2.9 Crypto/perf DoS hardening (packet sizes + allocations)
- [ ] Add test: crypto rejects oversized packets early (before large allocations) (`ZTSharp/ZeroTier/Protocol/ZeroTierPacketCrypto.cs`, `ZTSharp/ZeroTier/Protocol/ZeroTierPacketCryptoAesGmacSiv.cs`)
- [ ] Fix: enforce max packet length and avoid per-packet large allocations on invalid traffic (especially in `Dearmor` paths)
- [ ] Add test/bench: Poly1305 path does not allocate proportional to packet size on invalid packets (`ZTSharp/ZeroTier/Protocol/ZeroTierPacketCrypto.cs`)
- [ ] Fix: reduce ToArray churn and avoid copying spans where possible (or limit it behind strict size checks)
- [ ] Add test: unauthenticated HELLO floods do not trigger unbounded CPU/alloc work (`ZTSharp/ZeroTier/Internal/ZeroTierDataplanePeerSecurity.cs`)
- [ ] Fix: add fast-path bounds/rate limiting for HELLO processing (and/or early reject by size/version) (`ZTSharp/ZeroTier/Internal/ZeroTierDataplanePeerSecurity.cs`)
- [ ] Add test: compressed flag with invalid payload does not allocate repeatedly without bound (`ZTSharp/ZeroTier/Protocol/ZeroTierPacketCompression.cs`)
- [ ] Fix: cap compression input length / reuse buffers / avoid per-packet fixed-size allocations where possible (`ZTSharp/ZeroTier/Protocol/ZeroTierPacketCompression.cs`)

### 2.10 Queue backpressure + drop policy visibility
- [ ] Add test: control-plane packets (HELLO/OK/WHOIS/MulticastGather/SYN) are not silently dropped under moderate load (`ZTSharp/ZeroTier/Transport/ZeroTierUdpTransport.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRuntime.cs`)
- [ ] Fix: split control vs data queues and/or switch critical paths away from `DropOldest` (or make drops observable and trigger reconnect) (`ZTSharp/ZeroTier/Transport/ZeroTierUdpTransport.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRuntime.cs`)
- [ ] Add tracing/metrics: record drop counts per queue and surface via `ZeroTierTrace` or injectable logger

---

## Phase 3 - Real stack: socket surface + lifecycle semantics (`ZTSharp/ZeroTier/**`)

### 3.1 `IPAddress.Any` / `IPv6Any` semantics
- [ ] Add test: `ListenTcpAsync(IPAddress.Any, port)` accepts connections to any managed IP (not only the first) (`ZTSharp/ZeroTier/Internal/ZeroTierSocketBindings.cs`)
- [ ] Fix: treat Any/IPv6Any as “bind all managed IPs of that family” (or document alternative) and adjust route registry accordingly
- [ ] Add test: binding same port on two different managed IPs of same family is supported (or explicitly rejected with a clear message) (`ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRouteRegistry.cs`)
- [ ] Add test: `ConnectTcpAsync(local: IPAddress.Any/IPv6Any, remote)` chooses a valid managed local IP rather than rejecting (`ZTSharp/ZeroTier/Internal/ZeroTierSocketTcpConnector.cs`)
- [ ] Fix: normalize `local` endpoints with `IPAddress.Any`/`IPv6Any` to a concrete managed IP (consistent with bind semantics) (`ZTSharp/ZeroTier/Internal/ZeroTierSocketTcpConnector.cs`)
- [ ] Add test: IPv6 `ScopeId` mismatches do not break bind/listen/connect matching when the address is not link-local (`ZTSharp/ZeroTier/Internal/ZeroTierSocketBindings.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierSocketTcpConnector.cs`)
- [ ] Fix: canonicalize IPv6 addresses for managed-IP comparisons (define policy for `ScopeId`) (`ZTSharp/ZeroTier/Internal/ZeroTierSocketBindings.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierSocketTcpConnector.cs`)

### 3.2 Accepted `RemoteEndPoint` availability
- [ ] Add test: accepted `ManagedSocket.RemoteEndPoint` is populated (or explicitly documented as unsupported) (`ZTSharp/ZeroTier/Sockets/ManagedTcpSocketBackend.cs`)
- [ ] Fix: plumb remote endpoint metadata from accept path (likely via SYN segment / route registry) and store it on the accepted backend

### 3.3 Connect/dispose wedge avoidance
- [ ] Add test: disposing a `ManagedSocket` while `ConnectAsync` is in-flight does not hang indefinitely (respects cancellation/timeouts) (`ZTSharp/ZeroTier/Sockets/ManagedTcpSocketBackend.cs`, `ZTSharp/ZeroTier/Sockets/ManagedSocketBackend.cs`)
- [ ] Fix: avoid holding init lock across potentially long connects, or make dispose/close interrupt connect
- [ ] Add test: `ZeroTierSocket.DisposeAsync` cannot wedge forever behind a stuck `JoinAsync`/runtime creation (`ZTSharp/ZeroTier/ZeroTierSocket.cs`)
- [ ] Fix: ensure join/runtime creation uses internal cancellation and dispose can abort in-flight operations

### 3.4 UDP socket dispose idempotency
- [ ] Add test: concurrent `DisposeAsync` calls never throw and do not race (`ZTSharp/ZeroTier/ZeroTierUdpSocket.cs`)
- [ ] Fix: make `ZeroTierUdpSocket.DisposeAsync` idempotent (interlocked guard; don’t dispose semaphore inside `finally`)

### 3.5 Timeout helper exception semantics
- [ ] Add test: an inner `OperationCanceledException` not caused by caller token is not always mapped to `TimeoutException` (`ZTSharp/ZeroTier/Internal/ZeroTierTimeouts.cs`)
- [ ] Fix: distinguish “timeout cancellation” vs other `OperationCanceledException` sources; preserve original exception when appropriate

### 3.6 Crypto dearmor mutation footgun (AES-GMAC-SIV)
- [ ] Add test: failed dearmor does not leave buffer mutated (or document it as a hard contract) (`ZTSharp/ZeroTier/Protocol/ZeroTierPacketCryptoAesGmacSiv.cs`)
- [ ] Fix (preferred): authenticate first, then decrypt, or decrypt into a temporary buffer and copy on success
- [ ] Audit call sites: ensure every `Dearmor(...) == false` path drops the packet and never parses “plaintext”

### 3.7 Socket API parity gaps (intentional vs accidental)
- [ ] Decide + document: `ManagedSocket.Shutdown(SocketShutdown)` semantics (currently closes regardless of `how`) (`ZTSharp/ZeroTier/Sockets/ManagedSocket.cs`)
- [ ] If supporting half-close: implement proper `Shutdown` behavior for `Send`/`Receive`
- [ ] Add tests: backlog handling and port-0 listen behavior are documented and enforced (`ZTSharp/ZeroTier/Sockets/ManagedTcpSocketBackend.cs`)

### 3.8 Real-stack HTTP connect behavior
- [ ] Add test: DNS with AAAA+A where first address blackholes does not stall unbounded (bounded connect attempt per address) (`ZTSharp/ZeroTier/Http/ZeroTierHttpMessageHandler.cs`)
- [ ] Fix: implement per-address connect timeout and/or Happy-Eyeballs-style parallel attempts (document behavior) (`ZTSharp/ZeroTier/Http/ZeroTierHttpMessageHandler.cs`)

---

## Phase 4 - Persistence + filesystem hardening (`ZTSharp/**`, `ZTSharp/ZeroTier/Internal/**`)

### 4.1 State root confinement vs symlinks/junctions (critical)
- [ ] Add test: junction/symlink inside state root cannot escape root confinement (`ZTSharp/FileStateStore.cs`)
- [ ] Fix: enforce “no reparse points” (Windows) / “no symlinks” (Unix) on path traversal for read/write/list; document limits

### 4.2 `planet`/`roots` alias delete semantics (“resurrection”)
- [ ] Add test: deleting `planet`/`roots` removes both physical representations and cannot resurrect on next read (`ZTSharp/FileStateStore.cs`)
- [ ] Fix: when deleting an alias key, delete both physical files if present

### 4.3 Unbounded reads / TOCTOU hardening
- [ ] Add size caps + streaming reads: eliminate unbounded `File.ReadAllText`/`File.ReadAllLines`/`File.ReadAllBytes` on attacker-controlled state files (`identity.secret`, `.ips.txt`, persisted netconf dict) (`ZTSharp/ZeroTier/Internal/ZeroTierSocketIdentityMigration.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierSocketStatePersistence.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierIdentityStore.cs`)
- [ ] Add tests: large state files fail fast without allocating full contents
- [ ] Reduce TOCTOU: open-and-read with a single handle and enforce max length while reading

### 4.4 AtomicFile reliability (silent failure)
- [ ] Add test: simulate repeated `File.Move` failure → write fails clearly (not silent success) (`ZTSharp/Internal/AtomicFile.cs`)
- [ ] Fix: after max retries, throw a meaningful exception (include last failure)
- [ ] Evaluate file sharing on Windows: ensure readers/writers use `FileShare.Delete` where appropriate

### 4.5 State root path policy + secret material permissions
- [ ] Add test: relative `StateRootPath` is normalized to a stable full path (or explicitly documented) (`ZTSharp/ZeroTier/Internal/ZeroTierSocketFactory.cs`)
- [ ] Fix: normalize state-root to full path early; avoid CWD-dependent persistence surprises (`ZTSharp/ZeroTier/Internal/ZeroTierSocketFactory.cs`)
- [ ] Decide + document: file permission/ACL policy for secret identity files (best-effort hardening where feasible) (`ZTSharp/Internal/NodeIdentityService.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierIdentityStore.cs`)
- [ ] Add tests/notes: ensure atomic replace does not accidentally weaken permissions (platform-dependent)

### 4.6 Key normalization edge cases (cross-platform)
- [ ] Add tests: invalid filename chars / reserved device names don’t cause traversal or confusing collisions (Windows/macOS/Linux) (`ZTSharp/StateStoreKeyNormalization.cs`, `ZTSharp/StateStorePrefixNormalization.cs`)
- [ ] Fix: decide policy (reject vs encode) for platform-invalid key segments and document it (`docs/PERSISTENCE.md`)

---

## Phase 5 - Transport + cross-platform endpoint handling (`ZTSharp/Transport/**`, `ZTSharp/ZeroTier/Transport/**`)

### 5.1 Endpoint normalization correctness
- [ ] Add test: `LocalEndpoint` on wildcard bind is not rewritten to loopback (or explicitly document “local-only” mode) (`ZTSharp/Transport/Internal/UdpEndpointNormalization.cs`)
- [ ] Fix: remove Any→Loopback rewriting for general-purpose transports; treat “unspecified remote endpoint” as invalid input (fail fast)

### 5.2 Canonicalize IPv4-mapped IPv6 endpoints
- [ ] Add test: peer endpoint equality and “public/private” classification is stable across v4 vs v4-mapped-v6 (`ZTSharp/ZeroTier/Internal/ZeroTierDirectEndpointSelection.cs`)
- [ ] Fix: canonicalize endpoints consistently (e.g., map v4-mapped-v6 → v4) before comparing/storing/selecting

### 5.3 OS UDP receive-loop resilience
- [ ] Add test: non-`ConnectionReset` `SocketException` does not kill OS UDP receive loop (`ZTSharp/Transport/Internal/OsUdpReceiveLoop.cs`)
- [ ] Fix: catch/log other `SocketException` values and continue (similar to `ZeroTierUdpTransport`)
- [ ] Fix: ensure `OsUdpNodeTransport.SendFrameAsync` can’t be derailed by one bad peer endpoint (catch send exceptions per peer)
- [ ] Add test: discovery replies do not block the receive loop under backpressure (`ZTSharp/Transport/Internal/OsUdpReceiveLoop.cs`)
- [ ] Fix: don’t await discovery reply sends inline on the receive loop (enqueue/async with exception capture) (`ZTSharp/Transport/Internal/OsUdpReceiveLoop.cs`)

### 5.4 Windows `SIO_UDP_CONNRESET` IOCTL correctness
- [ ] Validate expected IOCTL input size on Windows and update to a compatible buffer (`ZTSharp/Transport/Internal/OsUdpSocketFactory.cs`)
- [ ] Add a Windows-only test or diagnostic assertion around IOCTL failure behavior (best-effort, non-fatal)

### 5.5 Socket creation resilience (IPv6-only vs dual-mode vs IPv4 fallback)
- [ ] Add test: when dual-mode bind fails, IPv6-only bind is attempted before falling back to IPv4 (or document the behavior) (`ZTSharp/Transport/Internal/OsUdpSocketFactory.cs`)
- [ ] Fix: improve fallback strategy and ensure endpoint families remain consistent across registry/sends

### 5.6 OS-UDP peer registry bounds
- [ ] Add test: peer registry does not grow without bound across node lifetimes and networks (`ZTSharp/Transport/Internal/OsUdpPeerRegistry.cs`)
- [ ] Fix: add TTL/eviction/bounds for `OsUdpPeerRegistry` (and remove `static` global directory if it causes leaks) (`ZTSharp/Transport/Internal/OsUdpPeerRegistry.cs`)

---

## Phase 6 - Legacy overlay stack correctness (`ZTSharp/Sockets/**`, `ZTSharp/Http/**`, `ZTSharp/Transport/**`)

### 6.1 Channel SingleWriter correctness under concurrency
- [ ] Add test: concurrent frame delivery (InMemory) does not violate channel writer assumptions (`ZTSharp/Transport/InMemoryNodeTransport.cs`, `ZTSharp/Sockets/OverlayTcpListener.cs`, `ZTSharp/Sockets/OverlayTcpIncomingBuffer.cs`, `ZTSharp/Sockets/ZtUdpClient.cs`)
- [ ] Fix: remove `SingleWriter=true` where multiple producers can write concurrently (or serialize producers explicitly)

### 6.2 Silent drop policy for TCP-like overlay streams
- [ ] Add test: large/slow HTTP response over overlay does not corrupt/hang due to silent drops (`ZTSharp/Sockets/OverlayTcpIncomingBuffer.cs`, `ZTSharp/Http/OverlayHttpMessageHandler.cs`)
- [ ] Fix: replace `DropWrite` with backpressure or explicit connection failure (surface to caller), at least for TCP/HTTP paths

### 6.3 HTTP stream disposal safety
- [x] Add test: disposing `HttpResponseMessage` never throws due to stream disposal (`ZTSharp/Http/OwnedOverlayTcpClientStream.cs`)
  - Test: `TunnelAndHttpTests.InMemoryOverlayHttpHandler_DisposingResponse_DoesNotThrowOrHang`
- [x] Fix: avoid blocking/synchronously waiting on async dispose in `Dispose(bool)`; ensure disposal is exception-safe
- [x] Add test: overlay HTTP connect failures always surface as `HttpRequestException` (not raw `TimeoutException`) (`ZTSharp/Http/OverlayHttpMessageHandler.cs`)
  - Test: `TunnelAndHttpTests.InMemoryOverlayHttpHandler_ConnectTimeout_IsHttpRequestException`
- [x] Fix: wrap overlay connect exceptions consistently (timeout/cancel/socket) (`ZTSharp/Http/OverlayHttpMessageHandler.cs`)
- [ ] Add test: overlay local-port allocator collisions are handled (retry/backoff) under concurrency (`ZTSharp/Http/OverlayHttpMessageHandler.cs`)
- [ ] Fix: add retry-on-collision (or increase range / document constraints) (`ZTSharp/Http/OverlayHttpMessageHandler.cs`)

### 6.4 Peer discovery framing collision risk
- [ ] Add test: payload that matches discovery magic does not get dropped as “control” when it’s actually application data (`ZTSharp/Transport/Internal/OsUdpPeerDiscoveryProtocol.cs`, `ZTSharp/Transport/Internal/OsUdpReceiveLoop.cs`)
- [ ] Fix: make discovery/control frames unambiguous (e.g., reserved frame type range + versioned header) or scope control frames to a dedicated channel/port

### 6.5 Legacy node lifecycle deadlocks + event isolation
- [ ] Add test: event handler re-entrancy cannot deadlock `StartAsync`/`StopAsync`/`JoinNetworkAsync` (`ZTSharp/Internal/NodeLifecycleService.cs`, `ZTSharp/Internal/NodeEventStream.cs`)
- [ ] Fix: don’t invoke user callbacks while holding lifecycle locks (queue + invoke outside lock, or async event dispatch)
- [ ] Add test: `Node.DisposeAsync` does not wedge indefinitely if stop paths are blocked (`ZTSharp/Internal/NodeLifecycleService.cs`)
- [ ] Add test: exceptions thrown by user `EventRaised` handlers do not fault node lifecycle operations (`ZTSharp/Internal/NodeEventStream.cs`)
- [ ] Fix: isolate/guard user callback exceptions and surface them via events/logging without faulting node (`ZTSharp/Internal/NodeEventStream.cs`)

### 6.6 EventLoop “poisoned” state
- [ ] Add test: one callback throwing does not permanently stop subsequent work without surfacing failure (`ZTSharp/EventLoop.cs`)
- [ ] Fix: either mark loop as faulted and reject further work, or keep running and isolate callback failures deterministically

### 6.9 ActiveTaskSet shutdown semantics
- [ ] Add test: one tracked task fault does not crash shutdown paths unexpectedly (`ZTSharp/Internal/ActiveTaskSet.cs`)
- [ ] Fix: decide whether `WaitAsync` should aggregate/ignore faults during shutdown; ensure all call sites use a cancellation token and don’t wedge (`ZTSharp/Internal/ActiveTaskSet.cs`, call sites in listeners/forwarders)

### 6.10 Overlay TCP background task safety
- [ ] Add test: overlay SYN-ACK send failures don’t produce unobserved task exceptions (`ZTSharp/Sockets/OverlayTcpListener.cs`)
- [ ] Fix: observe/handle fire-and-forget tasks (send/dispose) and ensure shutdown doesn’t hang (`ZTSharp/Sockets/OverlayTcpListener.cs`, `ZTSharp/Sockets/OverlayTcpClient.cs`)
- [ ] Add test: overlay `OverlayTcpClient.DisposeAsync` is bounded and does not hang even if FIN send fails (`ZTSharp/Sockets/OverlayTcpClient.cs`)
- [ ] Fix: ensure dispose uses a bounded timeout/cancellation and does not block indefinitely on transport send (`ZTSharp/Sockets/OverlayTcpClient.cs`)

### 6.11 Overlay protocol spoofing / authentication (security policy)
- [ ] Decide + document threat model: overlay stack is insecure by design vs should provide basic authenticity (`docs/USAGE.md`, `docs/COMPATIBILITY.md`)
- [ ] If securing: add message authentication and bind `SourceNodeId` to endpoint/identity; reject spoofed frames (`ZTSharp/Transport/NodeFrameCodec.cs`, `ZTSharp/Transport/Internal/OsUdpReceiveLoop.cs`, `ZTSharp/Transport/Internal/OsUdpPeerDiscoveryProtocol.cs`)
- [ ] Add tests: spoofed `SourceNodeId` frames do not reach overlay TCP/UDP handlers (`ZTSharp.Tests`)

### 6.7 Network leave ordering (transport subscription leak)
- [ ] Add test: `LeaveNetworkAsync` failure does not lose registration and does not leak transport subscription (`ZTSharp/Internal/NodeNetworkService.cs`)
- [ ] Fix: remove registration only after successful transport leave (or add compensating cleanup on failure)

### 6.8 InMemory transport cancellation-token semantics
- [ ] Add test: canceling sender token does not cause partial fanout/receiver drops in InMemory transport (`ZTSharp/Transport/InMemoryNodeTransport.cs`)
- [ ] Fix: use a transport-owned token for delivery, not the sender’s cancellation token

---

## Phase 7 - Docs + test stability

- [ ] Update docs to match behavior for direct endpoints / NAT traversal claims (`docs/COMPATIBILITY.md`, `docs/ZEROTIER_STACK.md`)
- [ ] Update docs to match port-0 semantics (supported where it is supported, and rejected where it is not) (`docs/ZEROTIER_SOCKETS.md`)
- [ ] Reduce test flakiness: replace `Task.Delay(...)` assertions with observable conditions or deterministic hooks (`ZTSharp.Tests/**`)
- [ ] Add missing unit tests called out by Fixes2 Tracking (no new E2E dependencies unless necessary)
