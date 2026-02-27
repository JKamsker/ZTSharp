# Compatibility

Known gaps between this library and upstream `libzt`.

---

## Real ZeroTier Stack

The managed stack (`ZTSharp.ZeroTier`) speaks enough of the real ZeroTier protocol to join
controller-based networks and provide user-space TCP/UDP sockets. The following gaps remain:

| Area | Gap |
|:-----|:----|
| OS adapter | No virtual network interface -- traffic is in-process only |
| Dataplane | Root-relayed by default. Experimental multipath mode supports hop-0 path learning, direct sends, RTT keepalives (`ECHO`), QoS measurement (`QOS_MEASUREMENT`), path negotiation (`PATH_NEGOTIATION_REQUEST`), and basic bonding policies. Still missing: full upstream Bond quality estimation (throughput/variance), ACK logic, and full parity with `ZeroTierOne` policy tuning. |
| Protocol coverage | Focused on join + IP dataplane + TCP/UDP socket MVP |
| Socket options | No `SocketOptionName` support (`NoDelay`, `KeepAlive`, etc.); some endpoint metadata differs from OS sockets |
| TCP performance | No congestion control or high-throughput send pipelines |

---

## Legacy Overlay Stack

The legacy stack (`ZTSharp`) is **not** protocol-compatible with the real ZeroTier network.
It uses a custom wire format and transport layer.

**Not implemented:**

- Real ZeroTier wire format, crypto, verbs, paths, NAT traversal
- Cryptographic authentication/encryption for legacy overlay frames
- Planet/roots processing or controller interaction
- OS virtual network interface (TUN/TAP)

**Implemented / partially implemented:**

| Feature | Notes |
|:--------|:------|
| Identity persistence | `identity.secret` / `identity.public` with 40-bit node ID |
| Network membership | `networks.d/*.conf` tracking |
| Overlay addressing | `networks.d/*.addr` persistence |
| Frame delivery | `InMemory` (single-process) and `OsUdp` (real UDP) transports |
| Peer directory | `peers.d/<NWID>/*.peer` persistence |
| UDP datagrams | `ZtUdpClient` over managed transport |
| TCP streams | `OverlayTcpClient` / `OverlayTcpListener` |
| Event loop | `EventLoop` scheduling primitives for future protocol state machines |
