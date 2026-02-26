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

- [ ] Record baseline: `dotnet build -c Release` and `dotnet test -c Release` (OS + date + summary)
- [ ] Add a single “Fixes2” tracking checklist mapping each fix → test(s) (`docs/Tasks/Fixes2/Tracking.md`)
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
- [ ] Add test: invalid-checksum UDP to registered port is dropped (`ZTSharp/ZeroTier/Net/UdpCodec.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierDataplaneIpHandler.cs`)
- [ ] Fix: verify UDP checksum on receive (IPv4 + IPv6) before delivering to UDP handlers
- [ ] Add test: invalid-checksum TCP SYN is dropped / does not trigger listener dispatch (`ZTSharp/ZeroTier/Net/TcpCodec.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierDataplaneIpHandler.cs`)
- [ ] Fix: use `TcpCodec.TryParseWithChecksum` for TCP receive paths that influence routing/state
- [ ] Add test: invalid-checksum ICMPv6 NS is dropped (`ZTSharp/ZeroTier/Internal/ZeroTierDataplaneIcmpv6Handler.cs`)
- [ ] Fix: validate ICMPv6 checksum for processed message types (Echo/NS/NA)

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

---

## Phase 3 - Real stack: socket surface + lifecycle semantics (`ZTSharp/ZeroTier/**`)

### 3.1 `IPAddress.Any` / `IPv6Any` semantics
- [ ] Add test: `ListenTcpAsync(IPAddress.Any, port)` accepts connections to any managed IP (not only the first) (`ZTSharp/ZeroTier/Internal/ZeroTierSocketBindings.cs`)
- [ ] Fix: treat Any/IPv6Any as “bind all managed IPs of that family” (or document alternative) and adjust route registry accordingly
- [ ] Add test: binding same port on two different managed IPs of same family is supported (or explicitly rejected with a clear message) (`ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRouteRegistry.cs`)

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

---

## Phase 4 - Persistence + filesystem hardening (`ZTSharp/**`, `ZTSharp/ZeroTier/Internal/**`)

### 4.1 State root confinement vs symlinks/junctions (critical)
- [ ] Add test: junction/symlink inside state root cannot escape root confinement (`ZTSharp/FileStateStore.cs`)
- [ ] Fix: enforce “no reparse points” (Windows) / “no symlinks” (Unix) on path traversal for read/write/list; document limits

### 4.2 `planet`/`roots` alias delete semantics (“resurrection”)
- [ ] Add test: deleting `planet`/`roots` removes both physical representations and cannot resurrect on next read (`ZTSharp/FileStateStore.cs`)
- [ ] Fix: when deleting an alias key, delete both physical files if present

### 4.3 Unbounded reads / TOCTOU hardening
- [ ] Add size caps + streaming reads: `identity.secret`, `.ips.txt`, persisted netconf dict (`ZTSharp/ZeroTier/Internal/ZeroTierSocketIdentityMigration.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierSocketStatePersistence.cs`, `ZTSharp/ZeroTier/Internal/ZeroTierIdentityStore.cs`)
- [ ] Add tests: large state files fail fast without allocating full contents
- [ ] Reduce TOCTOU: open-and-read with a single handle and enforce max length while reading

### 4.4 AtomicFile reliability (silent failure)
- [ ] Add test: simulate repeated `File.Move` failure → write fails clearly (not silent success) (`ZTSharp/Internal/AtomicFile.cs`)
- [ ] Fix: after max retries, throw a meaningful exception (include last failure)
- [ ] Evaluate file sharing on Windows: ensure readers/writers use `FileShare.Delete` where appropriate

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

### 5.4 Windows `SIO_UDP_CONNRESET` IOCTL correctness
- [ ] Validate expected IOCTL input size on Windows and update to a compatible buffer (`ZTSharp/Transport/Internal/OsUdpSocketFactory.cs`)
- [ ] Add a Windows-only test or diagnostic assertion around IOCTL failure behavior (best-effort, non-fatal)

### 5.5 Socket creation resilience (IPv6-only vs dual-mode vs IPv4 fallback)
- [ ] Add test: when dual-mode bind fails, IPv6-only bind is attempted before falling back to IPv4 (or document the behavior) (`ZTSharp/Transport/Internal/OsUdpSocketFactory.cs`)
- [ ] Fix: improve fallback strategy and ensure endpoint families remain consistent across registry/sends

---

## Phase 6 - Legacy overlay stack correctness (`ZTSharp/Sockets/**`, `ZTSharp/Http/**`, `ZTSharp/Transport/**`)

### 6.1 Channel SingleWriter correctness under concurrency
- [ ] Add test: concurrent frame delivery (InMemory) does not violate channel writer assumptions (`ZTSharp/Transport/InMemoryNodeTransport.cs`, `ZTSharp/Sockets/OverlayTcpListener.cs`, `ZTSharp/Sockets/OverlayTcpIncomingBuffer.cs`, `ZTSharp/Sockets/ZtUdpClient.cs`)
- [ ] Fix: remove `SingleWriter=true` where multiple producers can write concurrently (or serialize producers explicitly)

### 6.2 Silent drop policy for TCP-like overlay streams
- [ ] Add test: large/slow HTTP response over overlay does not corrupt/hang due to silent drops (`ZTSharp/Sockets/OverlayTcpIncomingBuffer.cs`, `ZTSharp/Http/OverlayHttpMessageHandler.cs`)
- [ ] Fix: replace `DropWrite` with backpressure or explicit connection failure (surface to caller), at least for TCP/HTTP paths

### 6.3 HTTP stream disposal safety
- [ ] Add test: disposing `HttpResponseMessage` never throws due to stream disposal (`ZTSharp/Http/OwnedOverlayTcpClientStream.cs`)
- [ ] Fix: avoid blocking/synchronously waiting on async dispose in `Dispose(bool)`; ensure disposal is exception-safe

### 6.4 Peer discovery framing collision risk
- [ ] Add test: payload that matches discovery magic does not get dropped as “control” when it’s actually application data (`ZTSharp/Transport/Internal/OsUdpPeerDiscoveryProtocol.cs`, `ZTSharp/Transport/Internal/OsUdpReceiveLoop.cs`)
- [ ] Fix: make discovery/control frames unambiguous (e.g., reserved frame type range + versioned header) or scope control frames to a dedicated channel/port

### 6.5 Legacy node lifecycle deadlocks + event isolation
- [ ] Add test: event handler re-entrancy cannot deadlock `StartAsync`/`StopAsync`/`JoinNetworkAsync` (`ZTSharp/Internal/NodeLifecycleService.cs`, `ZTSharp/Internal/NodeEventStream.cs`)
- [ ] Fix: don’t invoke user callbacks while holding lifecycle locks (queue + invoke outside lock, or async event dispatch)
- [ ] Add test: `Node.DisposeAsync` does not wedge indefinitely if stop paths are blocked (`ZTSharp/Internal/NodeLifecycleService.cs`)

### 6.6 EventLoop “poisoned” state
- [ ] Add test: one callback throwing does not permanently stop subsequent work without surfacing failure (`ZTSharp/EventLoop.cs`)
- [ ] Fix: either mark loop as faulted and reject further work, or keep running and isolate callback failures deterministically

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
