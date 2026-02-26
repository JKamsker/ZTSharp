# Fix correctness/security gaps across managed + legacy stacks

## Maintenance (this file)
- [x] Normalize formatting (indentation, separators, ASCII punctuation)
- [x] Convert Phase 0-2 items to checkboxes
- [x] Convert Phase 3-5 items to checkboxes
- [x] Convert Phase 6-7 items to checkboxes

## Summary

Implement fixes for all Critical->Low issues found in both stacks (ZTSharp.ZeroTier "real ZeroTier" + ZTSharp legacy overlay), with cross-platform tests and
CI green on Ubuntu/Windows/macOS. Priority includes: filesystem traversal hardening, persistence durability, user-space TCP correctness, dataplane
resilience/DoS hardening, API disposal semantics, and legacy overlay protocol correctness.

---

## Scope (confirmed)

- Stacks: Managed ZeroTier stack and legacy overlay stack.
- Depth: Fix all severities (correctness, resilience, DoS hardening, and key perf hotspots).
- Planet policy: Hardened default: max-size guards; verify state planet as a signed update of embedded planet when possible; PlanetSource=FilePath treated
  as user-trusted (still sanity-checked).

---

## Phase 0 - Baseline / guardrails

- [x] Run `dotnet restore`, `dotnet build -c Release`, `dotnet test -c Release`, `dotnet format --verify-no-changes` to capture baseline.
- [x] Create a tracking checklist (in PR description or `docs/Tasks/...` as appropriate) mapping each fix -> test case.

Acceptance: baseline results recorded; no repo-tracked changes yet.

Baseline (2026-02-25, Windows):

- `dotnet restore`: OK
- `dotnet build -c Release`: OK (0 warnings)
- `dotnet test -c Release`: OK (96 passed, 6 skipped)
- `dotnet format --verify-no-changes`: OK

---

## Phase 1 - Persistence + filesystem hardening (security + durability)

### 1.1 Key normalization: prevent rooted paths + ADS

Files

- ZTSharp/StateStoreKeyNormalization.cs
- ZTSharp/StateStorePrefixNormalization.cs

Changes

- [x] Reject keys/prefixes containing `:` (Windows drive roots + NTFS ADS).
- [x] Reject keys/prefixes that are rooted (`Path.IsPathRooted(...)` after normalization).
- [x] Reject keys/prefixes containing `\0` (defense-in-depth).
- [x] Reject keys/prefixes still containing `.` or `..` segments (already enforced).
- [x] Keep returned normalized form `a/b/c` (unchanged).

### 1.2 FileStateStore: root confinement + alias correctness + atomic writes

Files

- ZTSharp/FileStateStore.cs
- ZTSharp/StateStorePlanetAliases.cs

Changes

- [x] Enforce root confinement: after combining, compute full path and ensure it stays under `_rootPath` (OrdinalIgnoreCase on Windows, Ordinal elsewhere).
- [x] Planet/roots alias semantics:
  - [x] Reads/Exists/Delete: prefer planet if present, else fallback to roots.
  - [x] Writes: always write to planet.
  - [x] Optional migration: if only roots exists, attempt atomic rename/move to planet once.
- [x] List behavior:
  - [x] Normalize returned entries to `/` separators.
  - [x] Ensure `ListAsync("")` includes both `planet` and `roots` when either exists (avoid duplicates; case-insensitive handling on Windows/macOS FS).
- [x] Replace non-atomic `WriteAllBytesAsync` with atomic replace.

### 1.3 Atomic file helper + apply to managed stack persistence

Add

- [x] Add `ZTSharp/Internal/AtomicFile.cs` (or similar internal location)

Behavior

- [x] Write to `path.tmp.<guid>` in the same directory, `Flush(true)`, then `File.Move(tmp, path, overwrite: true)`; cleanup tmp on failure.

Apply

- [x] `ZTSharp/ZeroTier/Internal/ZeroTierIdentityStore.cs` Save/TryLoad:
  - [x] Catch `IOException`/`UnauthorizedAccessException` in `TryLoad` and return `false`.
  - [x] Use atomic write in `Save`.
- [x] `ZTSharp/ZeroTier/Internal/ZeroTierSocketStatePersistence.cs` PersistNetworkState:
  - [x] Atomic write for `.netconf.dict` and `.ips.txt` (write temp then replace).
  - [x] Add max-size cap when loading `.netconf.dict` (see Phase 5.2 constants).

### 1.4 Fix StateStore list edge cases (duplicates/case)

Files

- ZTSharp/MemoryStateStore.cs
- ZTSharp/FileStateStore.cs

Changes

- [x] Ensure alias insertions are deduped and case-normalized consistently.

### Tests (Phase 1)

Add/extend:

- [x] Add/extend `ZTSharp.Tests/StateStoreTests.cs` (or new `FileStateStoreSecurityTests.cs`) with:
  - [x] Windows-only: `WriteAsync("C:/Windows/Temp/pwn", ...)` throws; no file created.
  - [x] Windows-only: `WriteAsync("planet:ads", ...)` throws.
  - [x] Cross-platform: `WriteAsync("/etc/passwd", ...)` throws (after normalization/root check).
  - [x] Alias: if only `roots` exists, `ReadAsync("planet")` returns data and `ListAsync("")` contains both keys.
  - [x] Root confinement: `ListAsync("C:/")` throws on Windows.
- [x] Durability: verify atomic replace (write old then new; ensure file content is either old or new, never partial) using controlled IO (best-effort test).

Acceptance: traversal blocked; alias semantics match docs; all persistence writes are atomic.

---

## Phase 2 - Managed user-space TCP correctness + robustness

### 2.1 Fix ACK-wait race + ACK==0 wrap bug

File

- ZTSharp/ZeroTier/Net/UserSpaceTcpSender.cs

Changes

- [x] Remove `ack == 0` early-return.
- [x] Make ACK waiting stable across retries:
  - [x] Track `_ackTarget` (expected cumulative ACK) and a single `_ackTcs` per send operation (not per retry).
  - [x] Always short-circuit if `_sendUna >= expectedAck` before waiting/retransmitting.
- [x] Ensure thread-safe access between receive loop and sender (use `Volatile.Read/Write` or `Interlocked` where appropriate).

### 2.2 Replace receiver channel with Pipe + proper out-of-order trimming

Files

- ZTSharp/ZeroTier/Net/UserSpaceTcpReceiver.cs
- ZTSharp/ZeroTier/Net/UserSpaceTcpReceiveLoop.cs
- ZTSharp/ZeroTier/Net/UserSpaceTcpServerReceiveLoop.cs

Changes

- [x] Use `System.IO.Pipelines.Pipe` for in-order byte stream (eliminates per-segment `byte[]` allocations for in-order traffic).
- [x] Keep `_outOfOrder` for ahead-of-window segments, but:
  - [x] After `_recvNext` advances, trim or drop any buffered segments whose start is now `< _recvNext` (modular comparisons), releasing reserved bytes so window recovers.
  - [x] Cap out-of-order bytes to the same receive-buffer limit.
- [x] Propagate terminal exceptions:
  - [x] If remote closes with error (RST/IO), allow draining buffered data, then throw stored exception on subsequent reads (distinguish EOF vs reset).
- [x] Update call sites to await `ProcessSegmentAsync(...)` if needed (avoid blocking flush).

### 2.3 Validate TCP checksum on receive

File

- ZTSharp/ZeroTier/Net/TcpCodec.cs

Changes

- [x] Add `TryParseWithChecksum(...)` overload taking `(srcIp, dstIp, segment)`:
  - [x] Validates checksum without allocations (treat checksum field as zero or validate ones'-complement rule).
- [x] In receive loops, use the validating parse so corrupted segments are dropped.

### 2.4 MSS negotiation

Files

- ZTSharp/ZeroTier/Net/TcpCodec.cs
- ZTSharp/ZeroTier/Net/UserSpaceTcpReceiveLoop.cs
- ZTSharp/ZeroTier/Net/UserSpaceTcpClient.cs
- ZTSharp/ZeroTier/Net/UserSpaceTcpSender.cs

Changes

- [x] Extend TCP parse to expose options span; parse MSS option from SYN/SYN-ACK and set effective MSS to `min(localMss, remoteMss)` for chunking.
- [x] Add sender API `UpdateEffectiveMss(ushort remoteMss)`.

### Tests (Phase 2)

Add/extend:

- [x] Add/extend `ZTSharp.Tests/UserSpaceTcp*` with:
  - [x] ACK race reproduction: delayed ACK arriving after timeout must still complete send.
  - [x] Wrap-around ACK==0 case: `iss=0xFFFF_FFFF -> expectedAck=0`; ACK(0) completes.
  - [x] Out-of-order overlap trimming: scenario 1100.. then 1050.. then 1000.. must not leak window.
  - [x] Error propagation: RST causes `ReadAsync` to throw after drain, not return 0.
  - [x] Checksum validation: flip one bit in segment -> dropped.
  - [x] MSS negotiation: peer advertises MSS 536 -> outbound chunks never exceed 536.

Acceptance: all TCP tests stable under stress; no deadlocks; window recovers; no spurious timeouts.

---

## Phase 3 - Managed dataplane resilience + DoS hardening + perf

### 3.1 Eliminate hot-path ToArray() copies by making UDP datagrams byte[]-backed

Files

- ZTSharp/ZeroTier/Transport/ZeroTierUdpDatagram.cs
- ZTSharp/ZeroTier/Transport/ZeroTierUdpTransport.cs
- ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRxLoops.cs
- ZTSharp/ZeroTier/Internal/ZeroTierDataplanePeerDatagramProcessor.cs
- ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRootClient.cs (call sites)
- Any other .Payload.ToArray() usage in managed dataplane

Changes

- [x] Change `ZeroTierUdpDatagram.Payload` to `byte[]` (internal type).
- [x] Update callers to work on the original array in-place for dearmor/decompress.

### 3.2 Bound all dataplane queues and drop instead of killing loops

Files

- ZTSharp/ZeroTier/Transport/ZeroTierUdpTransport.cs (incoming queue)
- ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRuntime.cs (peer queue)
- ZTSharp/ZeroTier/Internal/ZeroTierRoutedIpv4Link.cs
- ZTSharp/ZeroTier/Internal/ZeroTierRoutedIpv6Link.cs

Decisions

- [x] Capacities:
  - [x] UDP incoming: 2048 datagrams, DropOldest.
  - [x] Peer queue: 2048 datagrams, DropOldest.
  - [x] Per-route incoming: 256 packets, DropOldest.
- [x] When channel write fails due to completion: exit loop; when due to full: drop and continue.

### 3.3 Keep peer loop alive on faults

File

- ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRxLoops.cs

Changes

- [x] Wrap `_peerDatagrams.ProcessAsync(...)` in try/catch; swallow/log non-cancellation exceptions and continue.

### 3.4 Avoid ingress HOL blocking on WHOIS

Files

- ZTSharp/ZeroTier/Internal/ZeroTierDataplanePeerDatagramProcessor.cs
- ZTSharp/ZeroTier/Internal/ZeroTierDataplanePeerSecurity.cs

Changes

- [x] Add `TryGetPeerKey(...)` fast path.
- [x] For non-HELLO packets where key missing:
  - [x] Kick off background `EnsurePeerKeyAsync(peerNodeId)` with rate limiting + negative caching.
  - [x] Drop current packet (peer will retransmit).
- [x] Replace global `_peerKeyLock` with per-peer in-flight task map:
  - [x] `ConcurrentDictionary<NodeId, Task<byte[]>> _inflightKeys`.
  - [x] On failure, remove so retries are possible.
- [x] Add bounded cache + TTL eviction:
  - [x] Max entries: 4096.
  - [x] TTL: 30 minutes.
  - [x] Negative TTL for failed WHOIS: 30 seconds.

### 3.5 Harden HELLO handling against CPU DoS

File

- ZTSharp/ZeroTier/Internal/ZeroTierDataplanePeerSecurity.cs

Changes

- [x] Reorder: parse minimum identity/public key -> compute shared key -> MAC/auth (Dearmor) -> only then run `LocallyValidate()`.
- [x] Clamp stored peer protocol version to supported range (`<= ZeroTierHelloClient.AdvertisedProtocolVersion`).

### 3.6 Root endpoint filtering (root-relayed mode hardening)

File

- ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRxLoops.cs

Changes

- [x] Pass `_rootEndpoint` into RxLoops; drop any datagrams not from that endpoint (prevents external injection).
- [x] Additionally, for `"source == rootNodeId"` path, require endpoint match before attempting root dearmor.

### 3.7 Fix ResolveNodeId cache bug

File

- ZTSharp/ZeroTier/Internal/ZeroTierDataplaneRootClient.cs

Changes

- [x] After selecting remoteNodeId, store `cache[managedIp] = remoteNodeId` (optionally with TTL if using a richer cache).

### 3.8 IPv6 scoped route key collision

File

- ZTSharp/ZeroTier/Internal/ZeroTierTcpRouteKeyV6.cs

Changes

- [x] Reject scoped/link-local addresses (`ScopeId != 0`) for route keys (throw `NotSupportedException`) to avoid collisions.

### Tests (Phase 3)

Add/extend:

- [x] Drop/queue tests: bounded queues don't grow unbounded under flood (use synthetic loops).
- [x] PeerLoop resilience: inject a peer processor that throws once; loop continues.
- [x] Root endpoint filtering: spoofed packets from non-root endpoint are dropped before crypto/WHOIS.
- [x] ResolveNodeId caching: second resolve hits cache (mock gather).

Acceptance: dataplane remains responsive under malformed/flood input; memory bounded; no loop death on single exception.

---

## Phase 4 - Managed socket surface + lifecycle semantics

### 4.1 ZeroTierSocket disposal race fix

File

- ZTSharp/ZeroTier/ZeroTierSocket.cs

Changes

- [x] Replace `_disposed` bool with `int _disposeState`.
- [x] `DisposeAsync`:
  - [x] `Interlocked.Exchange` guard (idempotent).
  - [x] Acquire `_joinLock` then `_runtimeLock` to avoid deadlock with Join->Runtime order.
  - [x] Dispose runtime safely.
  - [x] Dispose semaphores after locks acquired/released (no in-flight releasers).
- [x] Ensure all public methods call a single `ThrowIfDisposed()`.

### 4.2 ZeroTierTcpListener dispose actually waits + AcceptAsync throws ObjectDisposedException

Files

- ZTSharp/ZeroTier/ZeroTierTcpListener.cs
- ZTSharp/Internal/ActiveTaskSet.cs

Changes

- [x] Fix `ActiveTaskSet.WaitAsync` snapshot race: if snapshot empty but `_tasks` not empty, continue looping; do not return early.
- [x] In `ZeroTierTcpListener.DisposeAsync`, wait using `CancellationToken.None` (optionally with a bounded timeout token distinct from `_shutdown`).
- [x] Wrap `AcceptAsync` to translate `ChannelClosedException` -> `ObjectDisposedException`.

### 4.3 Normalize IPAddress.Any/IPv6Any for ListenTcpAsync and BindUdpAsync

Files

- ZTSharp/ZeroTier/ZeroTierSocket.cs
- ZTSharp/ZeroTier/Internal/ZeroTierSocketBindings.cs (as needed)

Changes

- [x] If caller passes Any/IPv6Any, map to default managed IP of that family (same policy as `ManagedSocketEndpointNormalizer`).

### 4.4 Reject invalid remote endpoints early

File

- ZTSharp/ZeroTier/Internal/ZeroTierSocketTcpConnector.cs

Changes

- [x] Reject `IPAddress.Any`, `IPv6Any`, multicast, and broadcast (where applicable) with clear exceptions.

### 4.5 Populate ManagedSocket.LocalEndPoint after connect without explicit bind

Files

- ZTSharp/ZeroTier/Internal/ZeroTierSocketTcpConnector.cs
- ZTSharp/ZeroTier/ZeroTierSocket.cs
- ZTSharp/ZeroTier/Sockets/ManagedTcpSocketBackend.cs

Changes

- [x] Add internal connect path returning `(Stream Stream, IPEndPoint LocalEndpoint)`.
- [x] Public `ZeroTierSocket.ConnectTcpAsync` still returns `Stream`.
- [x] `ManagedTcpSocketBackend.ConnectAsync` uses internal path so `_localEndPoint` is set to the chosen ephemeral endpoint.

### Tests (Phase 4)

- [x] Dispose concurrency: concurrent JoinAsync/ConnectTcpAsync + DisposeAsync does not throw unexpected ObjectDisposedException.
- [x] Listener dispose: ensure dispose waits for tracked tasks (create a slow accept handler).
- [x] Any normalization: ListenTcpAsync(IPAddress.Any, port) succeeds post-join.
- [x] ManagedSocket LocalEndPoint set after connect.

Acceptance: no disposal races; API semantics consistent and predictable.

---

## Phase 5 - Managed protocol/crypto hardening

### 5.1 Planet/world max-size guards + update-signature verification (when possible)

Files

- ZTSharp/ZeroTier/Internal/ZeroTierPlanetLoader.cs
- ZTSharp/ZeroTier/Protocol/ZeroTierWorldCodec.cs
- ZTSharp/ZeroTier/Protocol/ZeroTierWorldSignature.cs (new helper; name TBD)

Decisions

- [x] MaxWorldBytes = 16384 hard cap for any planet/roots bytes loaded from disk/state.

Changes

- [x] `ZeroTierWorldCodec.Decode`:
  - [x] Reject inputs `> MaxWorldBytes` with `FormatException`.
- [x] `ZeroTierPlanetLoader.Load`:
  - [x] Always decode embedded default first when PlanetSource=EmbeddedDefault.
  - [x] If state candidate present, decode only if size <= cap and structure valid.
  - [x] Verify candidate as a valid update of embedded default when possible:
    - [x] Same Type and Id.
    - [x] Timestamp strictly newer.
    - [x] Signature verifies using embedded default's `UpdatesMustBeSignedBy` over `SerializeForSign(candidate)` (sentinel prefix/suffix + world fields excluding signature, matching upstream `World::serialize(forSign=true)`).
  - [x] If verification fails, ignore candidate and use embedded default.
- [x] PlanetSource=FilePath: still enforce size cap + structural validity, but treat as trusted (no chain validation).

### 5.2 Cap network config dictionary total length to prevent OOM

File

- ZTSharp/ZeroTier/Internal/ZeroTierNetworkConfigProtocol.cs

Decisions

- [x] MaxNetworkConfigBytes = 1 * 1024 * 1024.

Changes

- [x] Before allocating `dictionary = new byte[configTotalLength]`, reject if `configTotalLength == 0` or `configTotalLength > MaxNetworkConfigBytes`.
- [x] Ensure `configTotalLength` fits `int`.

### 5.3 X25519 all-zero shared secret guard

File

- ZTSharp/ZeroTier/Protocol/ZeroTierC25519.cs

Changes

- [x] After CalculateAgreement, if rawKey is all-zero, throw `CryptographicException` (or return failure via new `TryAgree` internal helper) and treat peer identity invalid.

### 5.4 Cap PUSH_DIRECT_PATHS parse output

File

- ZTSharp/ZeroTier/Protocol/ZeroTierPushDirectPathsCodec.cs

Changes

- [ ] Clamp parsed path count to a small maximum (e.g., 32) even if packet claims more.

### Tests (Phase 5)

- [x] World signature helper: create synthetic ZeroTierWorld + sign key, verify SerializeForSign + VerifySignature succeeds; invalid byte flip fails.
- [x] PlanetLoader hardened behavior: invalid/oversized state planet ignored in favor of embedded.
- [x] NetworkConfig cap: absurd totalLength rejected without allocation.
- [x] All-zero shared secret: known small-order pubkey causes failure.

Acceptance: planet loading is bounded and hardened; config fetch cannot OOM; crypto rejects invalid DH.

---

## Phase 6 - Legacy overlay stack fixes (correctness + resilience + DoS bounds)

### 6.1 Serialize node operations against lifecycle stop (no check-then-act races)

Files

- ZTSharp/Internal/NodeLifecycleService.cs
- ZTSharp/Internal/NodeCore.cs

Changes

- [ ] Add `ExecuteWhileRunningAsync(...)` on `NodeLifecycleService` that holds `_stateLock` for the duration of an operation.
- [ ] Update `NodeCore.JoinNetworkAsync/LeaveNetworkAsync/SendFrameAsync/...` to use this wrapper so `StopAsync` cannot interleave and leave partial registrations.

### 6.2 Isolate user callbacks so receive loop can't be killed

Files

- ZTSharp/Internal/NodeTransportService.cs
- ZTSharp/Transport/OsUdpNodeTransport.cs
- ZTSharp/Transport/Internal/OsUdpReceiveLoop.cs

Changes

- [ ] Wrap `_onRawFrameReceived` and `_onFrameReceived` in try/catch; publish a fault event/log and continue.
- [ ] In `OsUdpNodeTransport.DispatchFrameAsync`, catch per-subscriber exception and continue.
- [ ] In `OsUdpReceiveLoop.RunAsync`, wrap `_dispatchFrameAsync` call to prevent loop death.

### 6.3 Peer discovery protocol: avoid false positives + spoof mismatch

Files

- ZTSharp/Transport/Internal/OsUdpPeerDiscoveryProtocol.cs
- ZTSharp/Transport/Internal/OsUdpReceiveLoop.cs

Changes

- [ ] Require payload length exactly `PayloadLength` for discovery frames.
- [ ] Require `discoveredNodeId == sourceNodeId` before registering, otherwise ignore.

### 6.4 Overlay TCP: local node id capture + handshake data loss + bounded queues

Files

- ZTSharp/Sockets/OverlayTcpListener.cs
- ZTSharp/Sockets/OverlayTcpClient.cs
- ZTSharp/Sockets/OverlayTcpIncomingBuffer.cs

Changes

- [ ] Remove captured `_localNodeId`; read `node.NodeId.Value` dynamically (or throw if NodeId==0 to force Start-before-use).
- [ ] Allow Data frames arriving before `_connected` to be buffered if they match the pending connection tuple.
- [ ] Make accept queue bounded (capacity 128, DropWrite).
- [ ] Make incoming buffer bounded (capacity 1024 segments, DropWrite) and enforce max frame payload length (reject oversized).

### 6.5 ZtUdpClient: fix "SendTo broadcasts to everyone" (protocol v2) + bounded receive + unsubscribe

File

- ZTSharp/Sockets/ZtUdpClient.cs

Changes

- [ ] Introduce UDP frame version 2 that includes destinationNodeId (ulong LE) in header:
  - [ ] v2 header: [ver=2][type=1][srcPort u16be][dstPort u16be][dstNodeId u64le] + payload
- [ ] Send v2 frames by default.
- [ ] Receive: parse v2 and require dstNodeId == localNodeId and dstPort == localPort.
- [ ] Back-compat: still parse v1 frames (treat as broadcast, same as old behavior) for mixed-version scenarios.
- [ ] Connected-mode filtering: if ConnectAsync used, only deliver datagrams from the connected (nodeId, port) pair.
- [ ] Make `_incoming` bounded (1024, DropWrite) so "drop if consumer can't keep up" is real.
- [ ] Always unsubscribe handler on dispose (ignore ownsConnection for event unsubscription to avoid leaks).

### 6.6 Fix ActiveTaskSet + forwarder disposal waits

Files

- ZTSharp/Internal/ActiveTaskSet.cs
- ZTSharp/Sockets/OverlayTcpPortForwarder.cs

Changes

- [ ] ActiveTaskSet.WaitAsync: never return early due to empty snapshot; honor cancellation by throwing (or return, but ensure disposal sites don't pass already-canceled token when they intend to wait).

### 6.7 Validate codec inputs on decode

Files

- ZTSharp/NetworkAddressCodec.cs (prefix range checks on decode)
- ZTSharp/PeerEndpointCodec.cs (reject port 0 on decode)

### 6.8 Event loop cancellation set growth

File

- ZTSharp/EventLoopTimerQueue.cs

Changes

- [ ] Only record cancellations for timers that actually exist; cap/prune cancelled set to prevent unbounded growth.

### Tests (Phase 6)

- [ ] Overlay TCP before start: constructing listener/client pre-start should either work (dynamic NodeId) or throw deterministically; test chosen behavior.
- [ ] Overlay TCP handshake data loss: server write immediately after accept must be received by client.
- [ ] ZtUdpClient: A->SendTo(B) must not be delivered to C when using v2 frames.
- [ ] OsUdp discovery: app payload starting with ZTC1 must still be delivered unless exact discovery frame length.
- [ ] Subscriber exception: throwing callback must not kill OS-UDP receive loop.
- [ ] ActiveTaskSet wait correctness under concurrency.

Acceptance: legacy overlay stack no longer has the identified correctness holes; receive loops stay alive under callback faults; queues are bounded.

---

## Phase 7 - Docs + final validation

- [ ] Update `docs/PERSISTENCE.md` to reflect:
  - [ ] Stricter key rules (no rooted paths/colon).
  - [ ] Alias behavior (planet/roots) and migration behavior.
  - [ ] Atomic write guarantees (best-effort).
- [ ] If overlay UDP frame v2 is introduced, document it briefly in `docs/USAGE.md` (or a legacy section).

Final acceptance:

- [ ] `dotnet format --verify-no-changes` passes.
- [ ] `dotnet test -c Release` passes on all OS (CI matrix).
- [ ] No new analyzer warnings (warnings-as-errors remains clean).

---

## Public API / compatibility notes

- FileStateStore will now throw on invalid keys (rooted/:/invalid segments) instead of silently writing outside root.
- Legacy overlay ZtUdpClient wire format changes (v2); v1 still accepted (broadcast semantics), but v2 fixes directed delivery.
- Managed stack public APIs remain stable; behavior becomes more deterministic (dispose semantics, Any binding normalization, better exceptions).

---

## Assumptions / defaults

- Managed dataplane remains root-relayed (so root-endpoint filtering is correct).
- PlanetSource=FilePath is explicitly user-trusted; we enforce size + structure sanity but don't enforce a chain-of-trust without an anchor.
- Queue capacities and caps chosen as:
    - MaxWorldBytes = 16384
    - MaxNetworkConfigBytes = 1MiB
    - Managed queues: 2048/2048/256 (DropOldest)
    - Legacy queues: 128/1024/1024 (DropWrite)
