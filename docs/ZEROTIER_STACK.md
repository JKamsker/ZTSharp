# Real ZeroTier stack (managed-only) — WIP

This repo currently contains two networking stacks:

- **Managed overlay stack** (`JKamsker.LibZt`): managed nodes talk to each other using this library's transport (`InMemory`/`OsUdp`). This is **not** protocol-compatible with the real ZeroTier network.
- **Real ZeroTier stack** (`JKamsker.LibZt.ZeroTier`): planned managed-only implementation that can join existing ZeroTier networks (normal NWIDs) without installing the OS ZeroTier client and without native binaries.

## Intended usage (MVP target)

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

MVP supports outbound IPv4 TCP connections to peers by their ZeroTier-managed IP, using a pure managed stack.

Right now:

- `ZtZeroTierSocket.JoinAsync()` joins a real network and persists assigned managed IPs.
- `ZtZeroTierSocket.ConnectTcpAsync(...)` can dial `http://<zt-ip>:<port>` through `HttpClient` (via `ZtZeroTierHttpMessageHandler`).

Current limitations:

- Outbound client-only (no listeners/port forwards yet).
- IPv4 only.
- Uses a single upstream root as a relay for simplicity (no direct path negotiation yet).

## CLI

The CLI accepts:

- `--stack managed` (current working managed overlay stack)
- `--stack zerotier` (WIP real ZeroTier stack)
- `--stack libzt` (alias for `zerotier`)

MVP is expected to support outbound `call` for the `zerotier` stack.
