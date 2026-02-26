using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Diagnostics;
using ZTSharp.ZeroTier;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.Tests;

public sealed class ZeroTierManagedSocketLifecycleTests
{
    [Fact]
    public async Task DisposeAsync_DoesNotDeadlock_WithConcurrentJoinAndConnect()
    {
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-managed-dispose-race-");
        var statePath = Path.Combine(stateRoot, "zerotier");
        Directory.CreateDirectory(statePath);

        var options = new ZeroTierSocketOptions { StateRootPath = stateRoot, NetworkId = 1 };
        var planet = ZeroTierWorldCodec.Decode(ZeroTierDefaultPlanet.World);
        var identity = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);
        await using var socket = new ZeroTierSocket(options, statePath, identity, planet);

        var joinLock = GetPrivateField<SemaphoreSlim>(socket, "_joinLock");
        await joinLock.WaitAsync();

        var joinTask = socket.JoinAsync();
        var connectTask = socket.ConnectTcpAsync(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 12345)).AsTask();
        var disposeTask = socket.DisposeAsync().AsTask();

        await WaitForDisposeStateAsync(socket, timeout: TimeSpan.FromSeconds(1));
        joinLock.Release();

        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        var joinEx = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await joinTask);
        Assert.NotNull(joinEx.ObjectName);

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await connectTask);
    }

    [Fact]
    public async Task ZeroTierTcpListener_Dispose_WaitsForTrackedTasks()
    {
        await using var runtime = CreateRuntime(localManagedIpV4: IPAddress.Parse("10.0.0.2"));
        await using var listener = new ZeroTierTcpListener(runtime, IPAddress.Parse("10.0.0.2"), localPort: 23456);

        var tasks = GetPrivateField<ZTSharp.Internal.ActiveTaskSet>(listener, "_connectionTasks");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tasks.Track(tcs.Task);

        var disposeTask = listener.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(disposeTask.IsCompleted);

        tcs.SetResult();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ZeroTierTcpListener_AcceptAsync_ThrowsObjectDisposedException_WhenDisposedWhileWaiting()
    {
        await using var runtime = CreateRuntime(localManagedIpV4: IPAddress.Parse("10.0.0.2"));
        await using var listener = new ZeroTierTcpListener(runtime, IPAddress.Parse("10.0.0.2"), localPort: 23457);

        var acceptTask = listener.AcceptAsync().AsTask();
        await Task.Delay(25);

        await listener.DisposeAsync();

        _ = await Assert.ThrowsAsync<ObjectDisposedException>(async () => await acceptTask);
    }

    [Fact]
    public async Task ListenTcpAsync_Any_IsNormalizedToManagedIp()
    {
        var networkId = 0x9ad07d01093a69e3UL;
        var localIp = IPAddress.Parse("10.0.0.2");
        var dict = BuildDictionaryWithMinimalComAndStaticIp(localIp, bits: 24);

        await using var runtime = CreateRuntime(localManagedIpV4: localIp);
        await using var socket = CreateJoinedSocket(runtime, networkId, localIp, dict);

        await using var listener = await socket.ListenTcpAsync(IPAddress.Any, port: 23458);
        Assert.Equal(new IPEndPoint(localIp, 23458), listener.LocalEndpoint);
    }

    [Fact]
    public async Task ManagedSocket_LocalEndPoint_IsPopulated_AfterConnect()
    {
        var networkId = 0x9ad07d01093a69e3UL;
        var rootNodeId = new NodeId(0x1111111111);
        var rootKey = RandomNumberGenerator.GetBytes(48);

        var identityA = ZeroTierTestIdentities.CreateFastIdentity(0x2222222222);
        var identityB = ZeroTierTestIdentities.CreateFastIdentity(0x3333333333);

        var ipA = IPAddress.Parse("10.0.0.1");
        var ipB = IPAddress.Parse("10.0.0.2");

        var dictA = BuildDictionaryWithMinimalComAndStaticIp(ipA, bits: 24);
        var dictB = BuildDictionaryWithMinimalComAndStaticIp(ipB, bits: 24);

        await using var rootUdp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        var rootEndpoint = rootUdp.LocalEndpoint;

        await using var udpA = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);
        await using var udpB = new ZeroTierUdpTransport(localPort: 0, enableIpv6: false);

        await using var runtimeA = new ZeroTierDataplaneRuntime(
            udpA,
            rootNodeId,
            rootEndpoint,
            rootKey,
            rootProtocolVersion: 12,
            localIdentity: identityA,
            networkId,
            localManagedIpV4: ipA,
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: ZeroTierInlineCom.GetInlineCom(dictA));

        await using var runtimeB = new ZeroTierDataplaneRuntime(
            udpB,
            rootNodeId,
            rootEndpoint,
            rootKey,
            rootProtocolVersion: 12,
            localIdentity: identityB,
            networkId,
            localManagedIpV4: ipB,
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: ZeroTierInlineCom.GetInlineCom(dictB));

        var identities = new Dictionary<NodeId, ZeroTierIdentity>
        {
            [identityA.NodeId] = identityA,
            [identityB.NodeId] = identityB
        };

        var groupToNodeId = new Dictionary<ZeroTierMulticastGroup, NodeId>
        {
            [ZeroTierMulticastGroup.DeriveForAddressResolution(ipA)] = identityA.NodeId,
            [ZeroTierMulticastGroup.DeriveForAddressResolution(ipB)] = identityB.NodeId
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rootTask = RunRootRelayAsync(rootUdp, rootNodeId, rootKey, networkId, identities, groupToNodeId, cts.Token);

        await using var socketA = CreateJoinedSocket(runtimeA, networkId, ipA, dictA);
        await using var socketB = CreateJoinedSocket(runtimeB, networkId, ipB, dictB);

        // Ensure B can decrypt the first SYN without waiting for background WHOIS.
        await runtimeB.SendEthernetFrameAsync(
            identityA.NodeId,
            etherType: 0x0000,
            frame: new byte[1],
            cancellationToken: CancellationToken.None);

        const int listenPort = 23459;
        await using var listener = await socketB.ListenTcpAsync(IPAddress.Any, listenPort, CancellationToken.None);

        await using var client = socketA.CreateSocket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(new IPEndPoint(ipB, listenPort), CancellationToken.None);

        var local = Assert.IsType<IPEndPoint>(client.LocalEndPoint);
        Assert.Equal(ipA, local.Address);
        Assert.InRange(local.Port, 1, ushort.MaxValue);

        await using var accepted = await listener.AcceptAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        var message = "ping"u8.ToArray();
        _ = await client.SendAsync(message, CancellationToken.None);

        var buffer = new byte[message.Length];
        var readTotal = 0;
        while (readTotal < buffer.Length)
        {
            var read = await accepted.ReadAsync(buffer.AsMemory(readTotal), CancellationToken.None);
            if (read == 0)
            {
                break;
            }

            readTotal += read;
        }

        Assert.Equal(message.Length, readTotal);
        Assert.True(buffer.AsSpan().SequenceEqual(message));

        cts.Cancel();
        await rootTask;
    }

    private static async Task RunRootRelayAsync(
        ZeroTierUdpTransport rootUdp,
        NodeId rootNodeId,
        byte[] rootKey,
        ulong networkId,
        IReadOnlyDictionary<NodeId, ZeroTierIdentity> identities,
        IReadOnlyDictionary<ZeroTierMulticastGroup, NodeId> groupToNodeId,
        CancellationToken cancellationToken)
    {
        var endpoints = new ConcurrentDictionary<NodeId, IPEndPoint>();

        while (!cancellationToken.IsCancellationRequested)
        {
            ZeroTierUdpDatagram datagram;
            try
            {
                datagram = await rootUdp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var packet = datagram.Payload;
            if (!ZeroTierPacketCodec.TryDecode(packet, out var decoded))
            {
                continue;
            }

            endpoints[decoded.Header.Source] = datagram.RemoteEndPoint;

            if (decoded.Header.Destination == rootNodeId)
            {
                var authPacket = (byte[])packet.Clone();
                if (!ZeroTierPacketCrypto.Dearmor(authPacket, rootKey))
                {
                    continue;
                }

                var verb = (ZeroTierVerb)(authPacket[ZeroTierPacketHeader.IndexVerb] & 0x1F);
                var payload = authPacket.AsSpan(ZeroTierPacketHeader.IndexPayload);

                if (verb == ZeroTierVerb.Whois)
                {
                    if (payload.Length < 5)
                    {
                        continue;
                    }

                    var targetNodeId = new NodeId(ZeroTierBinaryPrimitives.ReadUInt40BigEndian(payload.Slice(0, 5)));
                    if (!identities.TryGetValue(targetNodeId, out var identity))
                    {
                        continue;
                    }

                    var identityBytes = ZeroTierIdentityCodec.Serialize(identity, includePrivate: false);
                    var okPayload = new byte[1 + 8 + identityBytes.Length];
                    okPayload[0] = (byte)ZeroTierVerb.Whois;
                    BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1, 8), decoded.Header.PacketId);
                    identityBytes.CopyTo(okPayload.AsSpan(9));

                    var okHeader = new ZeroTierPacketHeader(
                        PacketId: 1,
                        Destination: decoded.Header.Source,
                        Source: rootNodeId,
                        Flags: 0,
                        Mac: 0,
                        VerbRaw: (byte)ZeroTierVerb.Ok);

                    var okPacket = ZeroTierPacketCodec.Encode(okHeader, okPayload);
                    ZeroTierPacketCrypto.Armor(okPacket, rootKey, encryptPayload: true);
                    await rootUdp.SendAsync(datagram.RemoteEndPoint, okPacket, cancellationToken);
                }
                else if (verb == ZeroTierVerb.MulticastGather)
                {
                    if (payload.Length < 23)
                    {
                        continue;
                    }

                    var reqNetworkId = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(0, 8));
                    if (reqNetworkId != networkId)
                    {
                        continue;
                    }

                    var mac = ZeroTierMac.Read(payload.Slice(9, 6));
                    var adi = BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(15, 4));
                    var group = new ZeroTierMulticastGroup(mac, adi);

                    if (!groupToNodeId.TryGetValue(group, out var member))
                    {
                        continue;
                    }

                    var okVerbHeaderLength = 1 + 8;
                    var okPayloadLength = okVerbHeaderLength + 8 + 6 + 4 + 4 + 2 + 5;
                    var okPayload = new byte[okPayloadLength];
                    okPayload[0] = (byte)ZeroTierVerb.MulticastGather;
                    BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(1, 8), decoded.Header.PacketId);

                    var ptr = okVerbHeaderLength;
                    BinaryPrimitives.WriteUInt64BigEndian(okPayload.AsSpan(ptr, 8), networkId);
                    ptr += 8;
                    group.Mac.CopyTo(okPayload.AsSpan(ptr, 6));
                    ptr += 6;
                    BinaryPrimitives.WriteUInt32BigEndian(okPayload.AsSpan(ptr, 4), group.Adi);
                    ptr += 4;
                    BinaryPrimitives.WriteUInt32BigEndian(okPayload.AsSpan(ptr, 4), 1u);
                    ptr += 4;
                    BinaryPrimitives.WriteUInt16BigEndian(okPayload.AsSpan(ptr, 2), 1);
                    ptr += 2;
                    ZeroTierBinaryPrimitives.WriteUInt40BigEndian(okPayload.AsSpan(ptr, 5), member.Value);

                    var okHeader = new ZeroTierPacketHeader(
                        PacketId: 2,
                        Destination: decoded.Header.Source,
                        Source: rootNodeId,
                        Flags: 0,
                        Mac: 0,
                        VerbRaw: (byte)ZeroTierVerb.Ok);

                    var okPacket = ZeroTierPacketCodec.Encode(okHeader, okPayload);
                    ZeroTierPacketCrypto.Armor(okPacket, rootKey, encryptPayload: true);
                    await rootUdp.SendAsync(datagram.RemoteEndPoint, okPacket, cancellationToken);
                }

                continue;
            }

            if (endpoints.TryGetValue(decoded.Header.Destination, out var destinationEndpoint))
            {
                await rootUdp.SendAsync(destinationEndpoint, packet, cancellationToken);
            }
        }
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "UDP transport ownership transfers to ZeroTierDataplaneRuntime, which is disposed by the caller.")]
    private static ZeroTierDataplaneRuntime CreateRuntime(IPAddress localManagedIpV4)
        => new(
            udp: new ZeroTierUdpTransport(localPort: 0, enableIpv6: false),
            rootNodeId: new NodeId(0x1111111111),
            rootEndpoint: new IPEndPoint(IPAddress.Loopback, 9999),
            rootKey: new byte[48],
            rootProtocolVersion: 12,
            localIdentity: ZeroTierTestIdentities.CreateFastIdentity(0x2222222222),
            networkId: 1,
            localManagedIpV4: localManagedIpV4,
            localManagedIpsV6: Array.Empty<IPAddress>(),
            inlineCom: new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 });

    private static ZeroTierSocket CreateJoinedSocket(
        ZeroTierDataplaneRuntime runtime,
        ulong networkId,
        IPAddress localManagedIpV4,
        byte[] networkConfigDictionaryBytes)
    {
        var stateRoot = TestTempPaths.CreateGuidSuffixed("zt-managed-socket-");
        var statePath = Path.Combine(stateRoot, "zerotier");
        Directory.CreateDirectory(statePath);

        var options = new ZeroTierSocketOptions { StateRootPath = stateRoot, NetworkId = networkId };
        var planet = ZeroTierWorldCodec.Decode(ZeroTierDefaultPlanet.World);
        var identity = ZeroTierTestIdentities.CreateFastIdentity(runtime.NodeId.Value);

        var socket = new ZeroTierSocket(options, statePath, identity, planet);

        SetPrivateField(socket, "_runtime", runtime);
        SetPrivateField(socket, "_networkConfigDictionaryBytes", networkConfigDictionaryBytes);
        SetPrivateField(socket, "_joined", true);
        SetPrivateField(socket, "<ManagedIps>k__BackingField", new[] { localManagedIpV4 });

        return socket;
    }

    private static byte[] BuildDictionaryWithMinimalComAndStaticIp(IPAddress address, int bits)
    {
        var endpoint = new IPEndPoint(address, bits);
        var inetLen = ZeroTierInetAddressCodec.GetSerializedLength(endpoint);
        var inet = new byte[inetLen];
        _ = ZeroTierInetAddressCodec.Serialize(endpoint, inet);

        // Minimal COM: version=1, qualifierCount=0, signedBy=0, no signature.
        var com = new byte[1 + 2 + 5];
        com[0] = 1;
        // remaining bytes are zero

        var escapedI = EscapeDictionaryValue(inet);
        var escapedC = EscapeDictionaryValue(com);

        var dict = new byte[(2 + escapedI.Length + 1) + (2 + escapedC.Length + 1)];
        var p = 0;

        dict[p++] = (byte)'I';
        dict[p++] = (byte)'=';
        escapedI.CopyTo(dict.AsSpan(p));
        p += escapedI.Length;
        dict[p++] = (byte)'\n';

        dict[p++] = (byte)'C';
        dict[p++] = (byte)'=';
        escapedC.CopyTo(dict.AsSpan(p));
        p += escapedC.Length;
        dict[p++] = (byte)'\n';

        return dict;
    }

    private static byte[] EscapeDictionaryValue(ReadOnlySpan<byte> value)
    {
        var output = new List<byte>(value.Length * 2);
        foreach (var b in value)
        {
            switch (b)
            {
                case 0:
                    output.Add((byte)'\\');
                    output.Add((byte)'0');
                    break;
                case 13:
                    output.Add((byte)'\\');
                    output.Add((byte)'r');
                    break;
                case 10:
                    output.Add((byte)'\\');
                    output.Add((byte)'n');
                    break;
                case (byte)'\\':
                    output.Add((byte)'\\');
                    output.Add((byte)'\\');
                    break;
                case (byte)'=':
                    output.Add((byte)'\\');
                    output.Add((byte)'e');
                    break;
                default:
                    output.Add(b);
                    break;
            }
        }

        return output.ToArray();
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        Assert.NotNull(value);
        return (T)value!;
    }

    private static void SetPrivateField(object instance, string fieldName, object value)
    {
        var field = instance.GetType().GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static async Task WaitForDisposeStateAsync(ZeroTierSocket socket, TimeSpan timeout)
    {
        var field = typeof(ZeroTierSocket).GetField("_disposeState", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var state = (int)field!.GetValue(socket)!;
            if (state != 0)
            {
                return;
            }

            await Task.Delay(1);
        }

        throw new TimeoutException("Timed out waiting for DisposeAsync to start.");
    }
}
