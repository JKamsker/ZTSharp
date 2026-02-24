# Usage Guide

This guide covers the public API for both networking stacks.
For ZeroTier-specific socket details, see [ZeroTier Sockets](ZEROTIER_SOCKETS.md).

---

## Table of Contents

- [Real ZeroTier Stack](#real-zerotier-stack)
  - [Join a Network](#join-a-network)
  - [HttpClient Integration](#httpclient-integration)
  - [TCP Sockets](#tcp-sockets)
  - [UDP Sockets](#udp-sockets)
- [Legacy Overlay Stack](#legacy-overlay-stack)
  - [Create a Node](#create-a-node)
  - [Join a Network (Overlay)](#join-a-network-overlay)
  - [Raw Frames](#raw-frames)
  - [UDP Datagrams](#udp-datagrams)
  - [TCP Port Forwarding](#tcp-port-forwarding)
  - [HttpClient over Overlay](#httpclient-over-overlay)
  - [Transport Modes](#transport-modes)

---

## Real ZeroTier Stack

The `JKamsker.LibZt.ZeroTier` namespace provides managed-only access to real ZeroTier networks.
No OS client installation required.

### Join a Network

```csharp
using JKamsker.LibZt.ZeroTier;

await using var zt = await ZtZeroTierSocket.CreateAsync(new ZtZeroTierSocketOptions
{
    StateRootPath = "path/to/state",
    NetworkId = 0x9ad07d01093a69e3UL
});
```

### HttpClient Integration

The simplest way to issue HTTP requests over a ZeroTier network:

```csharp
using var http = zt.CreateHttpClient();
var body = await http.GetStringAsync("http://10.121.15.99:5380/");
```

For more control, use `SocketsHttpHandler.ConnectCallback` to plug the managed TCP stream
into .NET's standard HTTP stack:

```csharp
using System.Net;
using System.Net.Http;

var handler = new SocketsHttpHandler { UseProxy = false };
handler.ConnectCallback = async (ctx, ct) =>
{
    var ip = IPAddress.Parse(ctx.DnsEndPoint.Host);
    return await zt.ConnectTcpAsync(new IPEndPoint(ip, ctx.DnsEndPoint.Port), ct);
};

using var http = new HttpClient(handler);
var body = await http.GetStringAsync("http://10.121.15.99:5380/");
```

### TCP Sockets

Connect to a remote peer:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

await using var socket = zt.CreateSocket(
    AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse("10.121.15.99"), 7777));
await socket.SendAsync(Encoding.UTF8.GetBytes("hello"));

var buffer = new byte[1024];
var read = await socket.ReceiveAsync(buffer);
```

Listen for incoming connections:

```csharp
var localIp = zt.ManagedIps.First(ip =>
    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

await using var listener = await zt.ListenTcpAsync(localIp, 7777);

var stream = await listener.AcceptAsync();
```

### UDP Sockets

```csharp
var localIp = zt.ManagedIps.First(ip =>
    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

await using var udp = await zt.BindUdpAsync(localIp, 9000);

await udp.SendToAsync(data, new IPEndPoint(remoteIp, 9000));
var result = await udp.ReceiveFromAsync(buffer);
```

> **Note:** See [ZeroTier Stack](ZEROTIER_STACK.md) for current capabilities and limitations.
> See [ZeroTier Sockets](ZEROTIER_SOCKETS.md) for the full API surface and known differences vs OS sockets.

**Runnable sample:** `samples/JKamsker.LibZt.Samples.ZeroTierSockets`

---

## Legacy Overlay Stack

The `JKamsker.LibZt` namespace provides a custom managed overlay transport.
This stack is **not** protocol-compatible with the real ZeroTier network.

### Create a Node

```csharp
using JKamsker.LibZt;

await using var node = new ZtNode(new ZtNodeOptions
{
    StateRootPath = "path/to/state",
    TransportMode = ZtTransportMode.InMemory, // or OsUdp
});

await node.StartAsync();
```

### Join a Network (Overlay)

```csharp
var networkId = 0x9ad07d010980bd45UL;
await node.JoinNetworkAsync(networkId);
```

### Raw Frames

```csharp
node.FrameReceived += (_, frame) =>
{
    // frame.Payload is ReadOnlyMemory<byte>
};

await node.SendFrameAsync(networkId, new byte[] { 1, 2, 3 });
```

### UDP Datagrams

`ZtUdpClient` multiplexes datagrams by a managed port pair inside the node-to-node transport.

```csharp
using JKamsker.LibZt.Sockets;

await using var udpA = new ZtUdpClient(nodeA, networkId, localPort: 10001);
await using var udpB = new ZtUdpClient(nodeB, networkId, localPort: 10002);

await udpA.ConnectAsync(nodeB.NodeId.Value, remotePort: 10002);
await udpB.ConnectAsync(nodeA.NodeId.Value, remotePort: 10001);

await udpA.SendAsync("ping"u8.ToArray());
var datagram = await udpB.ReceiveAsync();
```

### TCP Port Forwarding

Accept overlay TCP connections and forward them to a local OS TCP endpoint (ngrok-like):

```csharp
using JKamsker.LibZt.Sockets;

await using var forwarder = new ZtOverlayTcpPortForwarder(
    node,
    networkId,
    overlayListenPort: 28080,
    targetHost: "127.0.0.1",
    targetPort: 5000);

await forwarder.RunAsync();
```

### HttpClient over Overlay

Use `HttpClient` over `ZtOverlayTcpClient` (host can be a node id like `0x0123456789`):

```csharp
using JKamsker.LibZt.Http;

using var http = new HttpClient(new ZtOverlayHttpMessageHandler(node, networkId));
var response = await http.GetStringAsync("http://0x0123456789:28080/hello");
```

To use overlay IP/hostname addressing, provide an address book:

```csharp
using JKamsker.LibZt.Http;
using System.Net;

var book = new ZtOverlayAddressBook();
book.Add(IPAddress.Parse("10.1.2.3"), remoteNodeId);

var handler = new ZtOverlayHttpMessageHandler(
    node,
    networkId,
    new ZtOverlayHttpMessageHandlerOptions { AddressBook = book });

using var http = new HttpClient(handler);
var response = await http.GetStringAsync("http://10.1.2.3:28080/hello");
```

### Transport Modes

| Mode | Description |
|:-----|:------------|
| `InMemory` | Single-process deterministic transport (tests/simulations) |
| `OsUdp` | Real OS UDP sockets between managed nodes |

For `OsUdp`, you can optionally pre-register peers:

```csharp
await node.AddPeerAsync(networkId, peerNodeId, peerUdpEndpoint);
```
