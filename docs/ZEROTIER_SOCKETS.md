# Managed ZeroTier socket API (userspace, no OS adapter)

This repo’s **real ZeroTier managed stack** (`JKamsker.LibZt.ZeroTier`) exposes in-process networking primitives that look and feel like sockets, but **do not create an OS-visible network adapter**.

## Building blocks

- `ZtZeroTierSocket`
  - Joins an existing (controller-based) ZeroTier network by NWID.
  - Resolves ZeroTier-managed IPs to peer node ids.
  - Creates TCP listeners, TCP outbound connections, and UDP bindings.
- `ZtZeroTierTcpListener` / `ZtZeroTierUdpSocket`
  - Low-level async APIs using `Stream` and datagrams.
- `ZtManagedSocket` (compat wrapper)
  - A small compatibility wrapper to ease porting from `System.Net.Sockets.Socket`-style code.
  - Implements a practical subset: `Bind`, `Listen`, `Accept`, `Connect`, `Send`, `Receive`, `SendTo`, `ReceiveFrom`, `Shutdown`, `Close`, `Dispose`.

## Example: TCP echo (socket-like)

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;
using JKamsker.LibZt.ZeroTier;

await using var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
{
    StateRootPath = "path/to/state",
    NetworkId = 0x9ad07d01093a69e3UL
});

await zt.JoinAsync();

await using var socket = zt.CreateSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("10.121.15.99"), 7777));

await socket.SendAsync(Encoding.UTF8.GetBytes("hello"));
var buffer = new byte[5];
var read = await socket.ReceiveAsync(buffer);
```

See `samples/JKamsker.LibZt.Samples.ZeroTierSockets` for runnable TCP/UDP/HTTP examples.

## Example: HttpClient via ConnectCallback

This avoids a custom `HttpMessageHandler` and plugs the managed TCP stream into .NET’s standard HTTP stack:

```csharp
using System.Net;
using System.Net.Http;
using JKamsker.LibZt.ZeroTier;

await using var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
{
    StateRootPath = "path/to/state",
    NetworkId = 0x9ad07d01093a69e3UL
});

using var sockets = new SocketsHttpHandler { UseProxy = false };
sockets.ConnectCallback = async (ctx, ct) =>
{
    if (!IPAddress.TryParse(ctx.DnsEndPoint.Host, out var ip))
    {
        throw new InvalidOperationException("This example expects an IP literal host.");
    }

    return await zt.ConnectTcpAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
};

using var http = new HttpClient(sockets);
var body = await http.GetStringAsync("http://10.121.15.99:5380/");
```

## Supported subset (practical)

- **Address families:** IPv4, IPv6
- **Socket types:** TCP stream, UDP datagram
- **TCP:**
  - connect → `Stream` (direct) or `ZtManagedSocket.ConnectAsync(...)`
  - listen/accept → `Stream` (direct) or `ZtManagedSocket.ListenAsync(...)` + `AcceptAsync(...)`
  - connect/accept support cancellation; convenience timeout overloads exist on:
    - `ZtZeroTierSocket.ConnectTcpAsync(..., TimeSpan timeout, ...)`
    - `ZtZeroTierTcpListener.AcceptAsync(TimeSpan timeout, ...)`
- **UDP:**
  - bind + `SendToAsync(...)` / `ReceiveFromAsync(...)`
  - `ReceiveFromAsync(..., TimeSpan timeout, ...)` exists on `ZtZeroTierUdpSocket`

## Known differences vs OS sockets

This is intentionally **not** a drop-in OS networking implementation.

- **No OS adapter / no system routing**
  - Only in-process code that uses these APIs will go over ZeroTier.
- **Local bind semantics differ**
  - `ZtZeroTierSocket` binds to one of its managed IPs.
  - `ZtManagedSocket.BindAsync(IPAddress.Any/IPv6Any, port)` maps “any” to the node’s first managed IP of that family.
  - Listening on port `0` is not supported (bind to a concrete port first).
- **Limited socket option surface**
  - No `SocketOptionName` support (e.g., `NoDelay`, `KeepAlive`, `Linger`, buffer sizes), no `Select`/`Poll`, etc.
- **Accepted connection endpoint metadata**
  - `ZtManagedSocket.RemoteEndPoint` is currently `null` for accepted sockets (the underlying listener hands off a `Stream`).
- **Performance**
  - This user-space TCP stack is correctness-oriented and currently lacks many OS optimizations (e.g., congestion control and high-throughput sender pipelines). Large transfers may be significantly slower than OS TCP.

