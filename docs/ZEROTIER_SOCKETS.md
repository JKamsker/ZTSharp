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

- `ZeroTierSocket` binds to one of its managed IPs.
- `ManagedSocket.BindAsync(IPAddress.Any, port)` maps to the node's first managed IPv4.
- `ManagedSocket.BindAsync(IPAddress.IPv6Any, port)` maps to the node's first managed IPv6.
- Port `0` (ephemeral) is **not supported** -- bind to a concrete port.

### Socket Options

No `SocketOptionName` support (`NoDelay`, `KeepAlive`, `Linger`, buffer sizes).
No `Select` or `Poll`.

### Accepted Connection Metadata

`ManagedSocket.RemoteEndPoint` is currently `null` for accepted sockets.
The underlying listener hands off a `Stream` without endpoint metadata.

### Performance

The user-space TCP stack is correctness-oriented.
It currently lacks OS optimizations like congestion control and high-throughput sender pipelines.
Large transfers may be significantly slower than OS TCP.
