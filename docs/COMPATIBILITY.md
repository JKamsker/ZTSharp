# Compatibility gaps vs upstream `libzt`

This repo contains:

- A managed re-implementation of parts of the `libzt` *API surface* (`JKamsker.LibZt`) and a managed transport for deterministic testing/experiments.
- An optional wrapper around the upstream `libzt` implementation (`JKamsker.LibZt.Libzt`, via `ZeroTier.Sockets`) for joining real ZeroTier networks.

The managed stack is **not** a protocol-compatible replacement for ZeroTier / `libzt` today.

## Managed stack gaps (non-exhaustive)

- ZeroTier protocol compatibility (wire format, crypto, packet verbs, paths, NAT traversal).
- Planet/roots processing and controller interaction.
- Virtual network interface / lwIP parity (no TUN/TAP device plumbing).
- IP assignment, routes, multicast rules, flow rules, DNS config propagation.
- Membership authorization enforcement (membership is not validated against controllers).

## Managed stack implemented / partially implemented

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

## Upstream `libzt` stack notes

When you use `JKamsker.LibZt.Libzt` (upstream `libzt` via `ZeroTier.Sockets`):

- Nodes join real ZeroTier networks, show up as real members in controllers, and receive managed IP assignments (queried via `ZtLibztNode.GetNetworkAddresses`).
- The assigned IPs exist **inside libzt** (user-space stack). This does not create/configure a TUN/TAP interface or OS routes. Use `ZeroTier.Sockets.Socket` / `ZtLibztHttpMessageHandler` for traffic.
