# Compatibility gaps vs upstream `libzt`

This project is a managed re-implementation of parts of the `libzt` *API surface* and a managed transport for deterministic testing and experiments.

It is **not** a drop-in protocol-compatible replacement for ZeroTier / `libzt` today.

## Not implemented (non-exhaustive)

- ZeroTier protocol compatibility (wire format, crypto, packet verbs, paths, NAT traversal).
- Planet/roots processing and controller interaction.
- Virtual network interface / lwIP parity (no TUN/TAP device plumbing).
- IP assignment, routes, multicast rules, flow rules, DNS config propagation.
- Membership authorization enforcement (membership is not validated against controllers).

## Implemented / partially implemented

- Deterministic identity persistence (`identity.secret` / `identity.public`) with a 40-bit node id.
- Network membership tracking (`networks.d/*.conf`).
- Overlay address model persistence (`networks.d/*.addr`).
- Managed node-to-node frame delivery:
  - `InMemory` transport (single-process)
  - `OsUdp` transport (real UDP sockets between managed nodes)
- Persisted OS UDP peer directory (`peers.d/<NETWORK_ID>/*.peer`).
- UDP-like application datagrams over the managed transport (`ZtUdpClient`).
- TCP-like overlay streams over the managed transport (`ZtOverlayTcpClient` / `ZtOverlayTcpListener`).
- Internal event loop/scheduling primitives (`ZtEventLoop`) for future protocol state machines.
