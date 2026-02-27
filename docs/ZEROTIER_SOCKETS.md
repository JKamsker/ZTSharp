# ZeroTier Sockets API

The real ZeroTier managed stack (`ZTSharp.ZeroTier`) exposes in-process networking primitives
that look and feel like sockets, but **do not create an OS-visible network adapter**.

For an overview of the stack's capabilities and limitations, see [ZeroTier Stack](ZEROTIER_STACK.md).

---

## Table of Contents

- [Building Blocks](#building-blocks)
- [TCP Example (Socket-like)](#tcp-example-socket-like)
- [HttpClient via ConnectCallback](#httpclient-via-connectcallback)
- [Supported API Surface](#supported-api-surface)
- [Differences vs OS Sockets](#differences-vs-os-sockets)

---

## Building Blocks

**`ZeroTierSocket`** -- the main entry point.
Joins a ZeroTier network, resolves managed IPs, and creates TCP/UDP primitives.

**`ZeroTierTcpListener`** / **`ZeroTierUdpSocket`** -- low-level async APIs using `Stream` and datagrams.

**`ManagedSocket`** -- a compatibility wrapper for porting `System.Net.Sockets.Socket`-style code.
Implements: `Bind`, `Listen`, `Accept`, `Connect`, `Send`, `Receive`, `SendTo`, `ReceiveFrom`, `Shutdown`, `Close`, `Dispose`.

---

## TCP Example (Socket-like)

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using ZTSharp.ZeroTier;

await using var zt = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
{
    StateRootPath = "path/to/state",
    NetworkId = 0x9ad07d01093a69e3UL
});

await zt.JoinAsync();

await using var socket = zt.CreateSocket(
    AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("10.121.15.99"), 7777));

await socket.SendAsync(Encoding.UTF8.GetBytes("hello"));
var buffer = new byte[5];
var read = await socket.ReceiveAsync(buffer);
```

**Runnable sample:** `samples/ZTSharp.Samples.ZeroTierSockets`

---

## HttpClient via ConnectCallback

This approach avoids a custom `HttpMessageHandler` and plugs the managed TCP stream
directly into .NET's standard HTTP stack:

```csharp
using System.Net;
using System.Net.Http;
using ZTSharp.ZeroTier;

await using var zt = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
{
    StateRootPath = "path/to/state",
    NetworkId = 0x9ad07d01093a69e3UL
});

var handler = new SocketsHttpHandler { UseProxy = false };
handler.ConnectCallback = async (ctx, ct) =>
{
    if (!IPAddress.TryParse(ctx.DnsEndPoint.Host, out var ip))
        throw new InvalidOperationException("This example expects an IP literal host.");

    return await zt.ConnectTcpAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
};

using var http = new HttpClient(handler);
var body = await http.GetStringAsync("http://10.121.15.99:5380/");
```

---

## Supported API Surface

### Address Families

IPv4 and IPv6 (if the network assigns IPv6 managed IPs).

### TCP

| Operation | API |
|:----------|:----|
| Connect | `ZeroTierSocket.ConnectTcpAsync(...)` or `ManagedSocket.ConnectAsync(...)` |
| Listen + Accept | `ZeroTierSocket.ListenTcpAsync(...)` or `ManagedSocket.ListenAsync(...)` + `AcceptAsync(...)` |
| Timeout overloads | `ConnectTcpAsync(..., TimeSpan timeout, ...)` and `AcceptAsync(TimeSpan timeout, ...)` |
| Cancellation | Supported on both connect and accept |

### UDP

| Operation | API |
|:----------|:----|
| Bind | `ZeroTierSocket.BindUdpAsync(...)` |
| Send / Receive | `ZeroTierUdpSocket.SendToAsync(...)` / `ReceiveFromAsync(...)` |
| Timeout overload | `ReceiveFromAsync(..., TimeSpan timeout, ...)` |

---

## Differences vs OS Sockets

This is intentionally **not** a drop-in OS networking replacement.

### No OS Adapter

Only in-process code using these APIs routes through ZeroTier.
There is no virtual network interface visible to the operating system.

### Bind Semantics

- TCP listeners can bind either to a specific managed IP or to a wildcard:
  - `ZeroTierSocket.ListenTcpAsync(IPAddress.Any, port)` / `ManagedSocket.BindAsync(IPAddress.Any, port)` + `ListenAsync(...)` create a wildcard listener that accepts connections addressed to any of the node's managed IPv4s.
  - `ZeroTierSocket.ListenTcpAsync(IPAddress.IPv6Any, port)` / `ManagedSocket.BindAsync(IPAddress.IPv6Any, port)` + `ListenAsync(...)` create a wildcard listener that accepts connections addressed to any of the node's managed IPv6s.
- TCP connect may use `IPAddress.Any` / `IPAddress.IPv6Any` as the local endpoint to mean "choose a managed IP" (the selected local endpoint is returned by APIs that surface it).
- TCP listen with port `0` is **not supported** (`NotSupportedException`).
- UDP bind supports port `0` (ephemeral); the bound local port is selected internally via `ZeroTierEphemeralPorts.Generate()` and can be read back from the socket.
- UDP bind is currently **per address family + port** (not per IP + port). A UDP socket may receive datagrams destined to any of the node's managed IPs on that port.
- `ReceiveFromAsync(...)` does not currently surface the local destination IP.

### Socket Options

No `SocketOptionName` support (`NoDelay`, `KeepAlive`, `Linger`, buffer sizes).
No `Select` or `Poll`.

### Half-close

The managed TCP stack does not currently support half-close:

- `ManagedSocket.Shutdown(SocketShutdown.Send)` and `.Shutdown(SocketShutdown.Receive)` throw `NotSupportedException`.
- `ManagedSocket.Shutdown(SocketShutdown.Both)` closes the connection.

### Accepted Connection Metadata

Accepted sockets populate `ManagedSocket.RemoteEndPoint` (peer IP/port).

### Performance

The user-space TCP stack is correctness-oriented.
It currently lacks OS optimizations like congestion control and high-throughput sender pipelines.
Large transfers may be significantly slower than OS TCP.

UDP receive queues are bounded and may drop datagrams under load.
