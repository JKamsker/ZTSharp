# Fixes1 - Fix → test mapping

This file tracks each fix area in `docs/Tasks/Fixes1/Plan.md` and the test(s) that cover it.
Tick an item once the corresponding test exists and passes in `dotnet test -c Release`.

## Phase 1 - Persistence + filesystem hardening

- [x] 1.1 Key normalization (rooted paths / ADS / NUL): `ZTSharp.Tests` (new: StateStore key normalization tests)
- [x] 1.2 FileStateStore root confinement + planet/roots alias semantics + atomic writes: `ZTSharp.Tests` (new: FileStateStore security + alias tests)
- [x] 1.3 AtomicFile helper + managed persistence atomicity: `ZTSharp.Tests` (new: persistence atomic write + size-cap tests)
- [x] 1.4 List dedupe/case normalization: `ZTSharp.Tests` (extend existing StateStore list tests)

## Phase 2 - Managed user-space TCP correctness + robustness

- [x] 2.1 ACK-wait race + ACK wrap: `ZTSharp.Tests` (new: UserSpaceTcpSender ACK sequencing tests)
- [x] 2.2 Receiver Pipe + out-of-order trimming: `ZTSharp.Tests` (new: receiver reassembly + trimming tests)
- [x] 2.3 TCP checksum validation: `ZTSharp.Tests` (new: checksum fail drops segment)
- [x] 2.4 MSS negotiation: `ZTSharp.Tests` (new: MSS clamp/negotiation tests)

## Phase 3 - Managed dataplane resilience + DoS hardening + perf

- [x] 3.1 Remove hot-path ToArray copies: `ZTSharp.Benchmarks` (bench) + `ZTSharp.Tests` (sanity)
- [x] 3.2 Bound queues + drop policy: `ZTSharp.Tests` (new: bounded queue + no-loop-death tests)
- [x] 3.3 Keep peer loop alive on faults: `ZTSharp.Tests` (new: fault isolation tests)
- [x] 3.4 Avoid ingress HOL blocking on WHOIS: `ZTSharp.Tests` (new: WHOIS concurrency tests)
- [x] 3.5 Harden HELLO CPU DoS: `ZTSharp.Tests` (new: HELLO bounds tests)
- [x] 3.6 Root endpoint filtering: `ZTSharp.Tests` (new: root filtering correctness tests)
- [x] 3.7 ResolveNodeId cache correctness: `ZTSharp.Tests` (new: cache invalidation test)
- [x] 3.8 IPv6 scoped route key collision: `ZTSharp.Tests` (new: scoped route key tests)

## Phase 4 - Managed socket surface + lifecycle semantics

- [x] 4.1 ZeroTierSocket disposal race: `ZTSharp.Tests` (new: dispose/receive race tests)
- [x] 4.2 TcpListener dispose waits + AcceptAsync throws ODE: `ZTSharp.Tests` (new: listener dispose semantics tests)
- [x] 4.3 Normalize Any/IPv6Any binding: `ZTSharp.Tests` (new: Any bind normalization tests)
- [x] 4.4 Reject invalid remote endpoints: `ZTSharp.Tests` (new: endpoint validation tests)
- [x] 4.5 Populate LocalEndPoint after connect: `ZTSharp.Tests` (new: LocalEndPoint populated test)

## Phase 5 - Managed protocol/crypto hardening

- [x] 5.1 Planet/world size guards + signature verification (when possible): `ZTSharp.Tests` (new: planet size + signature behavior tests)
- [x] 5.2 Cap network config dictionary total length: `ZTSharp.Tests` (new: netconf size cap tests)
- [x] 5.3 X25519 all-zero shared secret guard: `ZTSharp.Tests` (new: all-zero secret reject test)
- [x] 5.4 Cap PUSH_DIRECT_PATHS parse output: `ZTSharp.Tests` (new: parse cap tests)

## Phase 6 - Legacy overlay stack fixes

- [x] 6.1 Serialize ops vs StopAsync interleaving: `ZTSharp.Tests` (new: lifecycle stop interleave tests)
- [x] 6.2 Callback isolation (receive loop survives): `ZTSharp.Tests` (new: callback throws does not kill loop)
- [x] 6.3 Discovery frame false positives/spoof mismatch: `ZTSharp.Tests` (new: discovery parsing tests)
- [x] 6.4 Overlay TCP handshake buffering + bounded queues: `ZTSharp.Tests` (new: handshake data loss + bounds tests)
- [x] 6.5 ZtUdpClient v2 directed delivery + bounds + unsubscribe: `ZTSharp.Tests` (new: directed delivery + dispose leak tests)
- [x] 6.6 ActiveTaskSet wait correctness: `ZTSharp.Tests` (new: WaitAsync concurrency test)
- [x] 6.7 Codec decode input validation: `ZTSharp.Tests` (new: decode rejects invalid inputs)
- [x] 6.8 Timer cancellation set growth bounds: `ZTSharp.Tests` (new: cancelled set cap test)

