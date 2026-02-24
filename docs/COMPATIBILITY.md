# Compatibility gaps vs upstream `libzt`

This repo contains two networking stacks:

- **Real ZeroTier stack (managed-only)** (`JKamsker.LibZt.ZeroTier`): speaks enough of the real ZeroTier protocol to join existing controller-based networks (normal NWIDs) and provide user-space TCP/UDP sockets over ZeroTier-managed IPs (IPv4/IPv6).
- **Legacy managed overlay stack** (`JKamsker.LibZt`): managed nodes communicate over this library's transports (`InMemory`/`OsUdp`). This is *not* protocol-compatible with the real ZeroTier network.

## Real ZeroTier stack gaps (vs upstream `libzt`)

- No OS-level virtual network adapter (traffic is handled in user space via `Stream`/`HttpClient`).
- Root-relayed dataplane only (no full direct path negotiation / NAT traversal yet).
- Limited verb/feature coverage (focused on join + IP dataplane + TCP/UDP sockets MVP).
- Limited socket option/metadata parity (e.g., no `SocketOptionName` support; accepted connections don't currently expose a reliable `RemoteEndPoint`).
- TCP performance/behavior is not OS-parity yet (no congestion control / high-throughput send pipelines).

## Legacy managed overlay stack gaps (non-exhaustive)

- Not protocol-compatible with the real ZeroTier network (wire format/crypto/verbs/paths/NAT traversal).
- No planet/roots processing or controller interaction.
- No OS virtual network interface / TUN/TAP plumbing.

## Legacy managed overlay stack implemented / partially implemented

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
