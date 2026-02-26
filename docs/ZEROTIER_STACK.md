# Real ZeroTier Stack

Managed-only implementation that joins existing controller-based ZeroTier networks (normal NWIDs)
without installing the OS ZeroTier client and without native binaries.

For the managed socket API surface and code examples, see [ZeroTier Sockets](ZEROTIER_SOCKETS.md).

---

## Quick Example

```csharp
using ZTSharp.ZeroTier;

await using var zt = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
{
    StateRootPath = "path/to/state",
    NetworkId = 0x9ad07d01093a69e3UL
});

using var http = zt.CreateHttpClient();
var body = await http.GetStringAsync("http://10.121.15.99:5380/");
```

---

## Capabilities

| Feature | Status |
|:--------|:-------|
| Join real ZeroTier networks by NWID | Supported |
| Managed IP persistence (IPv4 + IPv6) | Supported |
| Outbound TCP connections | Supported |
| TCP listeners (accept from OS ZeroTier clients) | Supported |
| UDP bind / send / receive | Supported |
| `HttpClient` integration | Supported |
| IPv4 and IPv6 | Supported |

---

## Current Limitations

- **No OS adapter** -- traffic is only visible to in-process callers using these APIs.
- **Root-relayed by default** -- when `ZeroTierSocketOptions.Multipath.Enabled` is `false` (default), traffic is root-relayed.
- **Experimental multipath** -- when enabled, the stack learns hop-0 physical paths and can send directly, with keepalive RTT (`ECHO`), QoS measurement (`QOS_MEASUREMENT`), path negotiation (`PATH_NEGOTIATION_REQUEST`), and optional bonding (`Multipath.BondPolicy`). This is not yet a full upstream `ZeroTierOne` bonding implementation.
- **Incomplete socket options** -- no `NoDelay`, `KeepAlive`, `Poll`, etc.; some endpoint metadata differs from OS sockets.
- **Performance** -- the user-space TCP stack is correctness-oriented and may be significantly slower for large transfers.

For a detailed comparison with upstream `libzt`, see [Compatibility](COMPATIBILITY.md).

---

## CLI Usage

The CLI supports both stacks via the `--stack` flag:

| Flag | Stack |
|:-----|:------|
| `--stack managed` | Real ZeroTier stack |
| `--stack zerotier` / `--stack libzt` | Aliases for `managed` |
| `--stack overlay` | Legacy managed overlay stack |

Managed stack CLI commands: `call`, `listen`, `udp-listen`, `udp-send`.

To enable experimental multipath/bonding for the managed stack, use the CLI flags:
`--multipath`, `--mp-bond`, `--mp-udp-sockets`, `--mp-udp-ports`, `--mp-warmup-root`.

---

## Two Stacks at a Glance

This repo contains two independent networking stacks:

| | Real ZeroTier Stack | Legacy Overlay Stack |
|:--|:--------------------|:---------------------|
| **Namespace** | `ZTSharp.ZeroTier` | `ZTSharp` |
| **Protocol** | Real ZeroTier (controller-based NWIDs) | Custom managed transport |
| **Interop** | Talks to OS ZeroTier clients | Only talks to other managed nodes |
| **Transport** | ZeroTier root servers | `InMemory` or `OsUdp` |
| **Use case** | Production networking | Testing and experimentation |
