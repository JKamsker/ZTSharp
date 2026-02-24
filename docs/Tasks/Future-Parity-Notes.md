# Future parity notes (out of Parity1 scope)

This file tracks protocol/node parity gaps that are **not** part of Parity1 (UDP/IPv6/TCP robustness/socket-like API).

## Full ZeroTier protocol / ZeroTierOne node parity
- **Complete verb coverage + state machines**
  - Implement/handle more root and peer verbs (e.g. `ACK`, `ECHO`, `QOS_MEASUREMENT`, `PATH_NEGOTIATION_*`, `NETWORK_CREDENTIALS`, `NETWORK_CONFIG` updates, `REMOTE_TRACE`, `USER_MESSAGE`, etc.).
  - Full peer lifecycle: link up/down detection, keepalives, roaming behavior, rate-limits, replay protection, etc.
- **Packet fragmentation/reassembly**
  - ZeroTier packet fragmentation flag handling + reassembly buffers/timeouts.
  - (Separately) IP-level fragmentation/PMTU handling at the user-space IP layer.
- **Crypto/protocol version parity**
  - Support newer protocol versions/cipher suites (beyond the current MVP constraints used for compatibility).
  - Strict interoperability testing across ZeroTierOne versions.
- **Dynamic network config + rules engine parity**
  - Apply network config changes over time (routes, rules, capabilities, COM refresh/rotation).
  - Full rules enforcement (filtering, isolation, flow rules) consistent with ZeroTierOne behavior.
- **Topology / world updates**
  - Planet/moon update handling with signatures and trust rules.
  - Better root selection, failover, and caching.
- **Performance features**
  - Bonding/multipath, path scoring, pacing, QoS metrics, and adaptive path selection.
  - Reduced allocations/copies in hot paths; buffer pooling.

## L2/L3 feature completeness (beyond Parity1)
- **Multicast/broadcast parity**
  - IGMP/MLD behavior, multicast subscriptions, and higher-fidelity L2 multicast handling.
- **Routing / bridging**
  - Advanced route management and bridging scenarios (still userspace, no OS adapter).
- **Service discovery behaviors**
  - mDNS/LLMNR/other discovery traffic patterns and how they should be surfaced/handled in userspace.

## “No OS adapter” constraint (explicit)
Even if the managed node can fully speak the ZeroTier protocol, an OS-visible adapter is still required for *transparent* integration with all OS networking stacks and arbitrary apps. This project intentionally avoids that; instead it provides **in-process** sockets/streams/handlers.

