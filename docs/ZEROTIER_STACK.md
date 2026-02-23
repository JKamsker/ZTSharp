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

The public API surface is scaffolded, but the real ZeroTier protocol implementation is **not implemented yet**.

Right now:

- `ZtZeroTierSocket.CreateAsync(...)` validates options and returns an instance.
- Any attempt to open a TCP connection (including via `HttpClient`) throws `NotSupportedException`.

## CLI

The CLI accepts:

- `--stack managed` (current working managed overlay stack)
- `--stack zerotier` (WIP real ZeroTier stack)
- `--stack libzt` (alias for `zerotier`)

MVP is expected to support outbound `call` for the `zerotier` stack.

