# Real ZeroTier stack (managed-only)

This repo contains two networking stacks:

- **Legacy managed overlay stack** (`JKamsker.LibZt`): managed nodes talk to each other using this library's transport (`InMemory`/`OsUdp`). This is **not** protocol-compatible with the real ZeroTier network.
- **Real ZeroTier stack** (`JKamsker.LibZt.ZeroTier`): managed-only implementation that can join existing controller-based ZeroTier networks (normal NWIDs) without installing the OS ZeroTier client and without native binaries.

## Usage

Join an existing network and issue an HTTP request to a peer by its ZeroTier-managed IP:

```csharp
using JKamsker.LibZt.ZeroTier;

await using var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
{
    StateRootPath = "path/to/state",
    NetworkId = 0x9ad07d01093a69e3UL
});

using var http = zt.CreateHttpClient();
var body = await http.GetStringAsync("http://10.121.15.99:5380/");
```

## Status

Managed stack supports:

- Joining a real network (`ZtZeroTierSocket.JoinAsync()`), persisting assigned managed IPs (IPv4 + optional IPv6).
- Outbound TCP (`ZtZeroTierSocket.ConnectTcpAsync(...)`) and `HttpClient` support (via `ZtZeroTierHttpMessageHandler` or `SocketsHttpHandler.ConnectCallback`).
- TCP listeners (`ZtZeroTierSocket.ListenTcpAsync(...)`) that OS ZeroTier clients can connect to using managed IPv4/IPv6.
- UDP bind/send/receive (`ZtZeroTierSocket.BindUdpAsync(...)` / `ZtZeroTierUdpSocket`).

Current limitations:

- **No OS adapter**: traffic is only visible to in-process callers using these APIs.
- **Dataplane path selection is minimal**: no full peer path negotiation / NAT traversal yet (root-relayed for simplicity).
- **Socket feature parity is incomplete**: no OS socket options (`NoDelay`, `KeepAlive`, `Poll`, etc.) and limited endpoint metadata for accepted sockets.
- **Performance is not OS-parity**: the user-space TCP stack is correctness-oriented and may be significantly slower for large transfers.

See `docs/ZEROTIER_SOCKETS.md` for the managed socket-like API surface and known differences vs OS sockets.

## CLI

The CLI accepts:

- `--stack managed` (real ZeroTier stack)
- `--stack overlay` (legacy managed overlay stack; needed for `expose` and the tunnel demo)
- `--stack zerotier` / `--stack libzt` (aliases for `managed`)

Managed stack supports `call`, `listen`, `udp-listen`, and `udp-send`.
