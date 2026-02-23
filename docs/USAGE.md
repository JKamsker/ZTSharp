# Usage guide

## Create and start a node

```csharp
using JKamsker.LibZt;

await using var node = new ZtNode(new ZtNodeOptions
{
    StateRootPath = "path/to/state",
    TransportMode = ZtTransportMode.InMemory, // or OsUdp
});

await node.StartAsync();
```

## Join a network

```csharp
var networkId = 0x9ad07d010980bd45UL;
await node.JoinNetworkAsync(networkId);
```

## Send/receive raw frames

```csharp
node.FrameReceived += (_, frame) =>
{
    // frame.Payload is ReadOnlyMemory<byte>
};

await node.SendFrameAsync(networkId, new byte[] { 1, 2, 3 });
```

## UDP-like datagrams over Zt transport

`ZtUdpClient` multiplexes datagrams by a (managed) port pair inside the node-to-node transport.

```csharp
using JKamsker.LibZt.Sockets;

await using var udpA = new ZtUdpClient(nodeA, networkId, localPort: 10001);
await using var udpB = new ZtUdpClient(nodeB, networkId, localPort: 10002);

await udpA.ConnectAsync(nodeB.NodeId.Value, remotePort: 10002);
await udpB.ConnectAsync(nodeA.NodeId.Value, remotePort: 10001);

await udpA.SendAsync("ping"u8.ToArray());
var datagram = await udpB.ReceiveAsync();
```

## Transport modes

- `ZtTransportMode.InMemory`: single-process deterministic transport (tests/simulations).
- `ZtTransportMode.OsUdp`: uses OS UDP sockets so two managed nodes can exchange packets over the real network stack.

For `OsUdp` you can optionally pre-register peers explicitly:

```csharp
await node.AddPeerAsync(networkId, peerNodeId, peerUdpEndpoint);
```

