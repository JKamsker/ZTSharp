using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using JKamsker.LibZt.ZeroTier.Protocol;
using JKamsker.LibZt.ZeroTier.Transport;

namespace JKamsker.LibZt.ZeroTier.Internal;

internal readonly record struct ZtZeroTierHelloOk(
    ZtNodeId RootNodeId,
    IPEndPoint RootEndpoint,
    ulong HelloPacketId,
    ulong HelloTimestampEcho,
    byte RemoteProtocolVersion,
    byte RemoteMajorVersion,
    byte RemoteMinorVersion,
    ushort RemoteRevision,
    IPEndPoint? ExternalSurfaceAddress);

internal static class ZtZeroTierHelloClient
{
    internal const byte AdvertisedProtocolVersion = 11; // <12 => avoid AES-GMAC-SIV for early MVP.
    internal const byte AdvertisedMajorVersion = 1;
    internal const byte AdvertisedMinorVersion = 12;
    internal const ushort AdvertisedRevision = 0;

    private const int OkIndexInReVerb = ZtZeroTierPacketHeader.Length;
    private const int OkIndexInRePacketId = OkIndexInReVerb + 1;
    private const int OkIndexPayload = OkIndexInRePacketId + 8;

    private const int HelloOkIndexTimestamp = OkIndexPayload;
    private const int HelloOkIndexProtocolVersion = HelloOkIndexTimestamp + 8;
    private const int HelloOkIndexMajorVersion = HelloOkIndexProtocolVersion + 1;
    private const int HelloOkIndexMinorVersion = HelloOkIndexMajorVersion + 1;
    private const int HelloOkIndexRevision = HelloOkIndexMinorVersion + 1;

    public static async Task<ZtZeroTierHelloOk> HelloRootsAsync(
        ZtZeroTierUdpTransport udp,
        ZtZeroTierIdentity localIdentity,
        ZtZeroTierWorld planet,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        var rootKeys = new Dictionary<ZtNodeId, byte[]>(planet.Roots.Count);
        foreach (var root in planet.Roots)
        {
            var key = new byte[48];
            ZtZeroTierC25519.Agree(localIdentity.PrivateKey, root.Identity.PublicKey, key);
            rootKeys[root.Identity.NodeId] = key;
        }

        var helloTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pending = new Dictionary<ulong, ZtNodeId>(capacity: planet.Roots.Count);

        foreach (var root in planet.Roots)
        {
            if (!rootKeys.TryGetValue(root.Identity.NodeId, out var key))
            {
                continue;
            }

            foreach (var endpoint in root.StableEndpoints)
            {
                var packet = BuildHelloPacket(
                    localIdentity,
                    destination: root.Identity.NodeId,
                    physicalDestination: endpoint,
                    planet,
                    helloTimestamp,
                    key,
                    out var packetId);

                try
                {
                    await udp.SendAsync(endpoint, packet, cancellationToken).ConfigureAwait(false);
                    pending[packetId] = root.Identity.NodeId;
                }
                catch (System.Net.Sockets.SocketException)
                {
                    // Some environments don't have IPv6 connectivity. Ignore send failures and wait for any
                    // reachable root to respond.
                }
            }
        }

        if (pending.Count == 0)
        {
            throw new InvalidOperationException("Failed to send HELLO to any root endpoints (no reachable network?).");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            ZtZeroTierUdpDatagram datagram;
            try
            {
                datagram = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for HELLO response after {timeout}.");
            }

            var packetBytes = datagram.Payload.ToArray();
            if (!ZtZeroTierPacketCodec.TryDecode(packetBytes, out var packet))
            {
                continue;
            }

            if (!rootKeys.TryGetValue(packet.Header.Source, out var key))
            {
                continue;
            }

            if (!ZtZeroTierPacketCrypto.Dearmor(packetBytes, key))
            {
                continue;
            }

            if ((packetBytes[27] & ZtZeroTierPacketHeader.VerbFlagCompressed) != 0)
            {
                if (!ZtZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                {
                    continue;
                }

                packetBytes = uncompressed;
            }

            var verb = (ZtZeroTierVerb)(packetBytes[27] & 0x1F);
            if (verb != ZtZeroTierVerb.Ok)
            {
                continue;
            }

            if (packetBytes.Length < HelloOkIndexRevision + 2)
            {
                continue;
            }

            var inReVerb = (ZtZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
            if (inReVerb != ZtZeroTierVerb.Hello)
            {
                continue;
            }

            var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
            if (!pending.TryGetValue(inRePacketId, out var rootNodeId))
            {
                continue;
            }

            var timestampEcho = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(HelloOkIndexTimestamp, 8));
            var remoteProto = packetBytes[HelloOkIndexProtocolVersion];
            var remoteMajor = packetBytes[HelloOkIndexMajorVersion];
            var remoteMinor = packetBytes[HelloOkIndexMinorVersion];
            var remoteRevision = BinaryPrimitives.ReadUInt16BigEndian(packetBytes.AsSpan(HelloOkIndexRevision, 2));

            var ptr = HelloOkIndexRevision + 2;
            IPEndPoint? surface = null;
            if (ptr < packetBytes.Length)
            {
                if (ZtZeroTierInetAddressCodec.TryDeserialize(packetBytes.AsSpan(ptr), out var parsed, out var consumed))
                {
                    surface = parsed;
                    ptr += consumed;
                }
            }

            return new ZtZeroTierHelloOk(
                RootNodeId: rootNodeId,
                RootEndpoint: datagram.RemoteEndPoint,
                HelloPacketId: inRePacketId,
                HelloTimestampEcho: timestampEcho,
                RemoteProtocolVersion: remoteProto,
                RemoteMajorVersion: remoteMajor,
                RemoteMinorVersion: remoteMinor,
                RemoteRevision: remoteRevision,
                ExternalSurfaceAddress: surface);
        }
    }

    public static async Task HelloAsync(
        ZtZeroTierUdpTransport udp,
        ZtZeroTierIdentity localIdentity,
        ZtZeroTierWorld planet,
        ZtNodeId destination,
        IPEndPoint physicalDestination,
        byte[] sharedKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(udp);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(planet);
        ArgumentNullException.ThrowIfNull(physicalDestination);
        ArgumentNullException.ThrowIfNull(sharedKey);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be positive.");
        }

        if (localIdentity.PrivateKey is null)
        {
            throw new InvalidOperationException("Local identity must contain a private key.");
        }

        var helloTimestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var packet = BuildHelloPacket(
            localIdentity,
            destination,
            physicalDestination,
            planet,
            helloTimestamp,
            sharedKey,
            out var packetId);

        await udp.SendAsync(physicalDestination, packet, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (true)
        {
            ZtZeroTierUdpDatagram datagram;
            try
            {
                datagram = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out waiting for HELLO response after {timeout}.");
            }

            var packetBytes = datagram.Payload.ToArray();
            if (!ZtZeroTierPacketCodec.TryDecode(packetBytes, out var decoded))
            {
                continue;
            }

            if (decoded.Header.Source != destination)
            {
                continue;
            }

            if (!ZtZeroTierPacketCrypto.Dearmor(packetBytes, sharedKey))
            {
                continue;
            }

            if ((packetBytes[27] & ZtZeroTierPacketHeader.VerbFlagCompressed) != 0)
            {
                if (!ZtZeroTierPacketCompression.TryUncompress(packetBytes, out var uncompressed))
                {
                    continue;
                }

                packetBytes = uncompressed;
            }

            var verb = (ZtZeroTierVerb)(packetBytes[27] & 0x1F);
            if (verb != ZtZeroTierVerb.Ok)
            {
                continue;
            }

            if (packetBytes.Length < HelloOkIndexRevision + 2)
            {
                continue;
            }

            var inReVerb = (ZtZeroTierVerb)(packetBytes[OkIndexInReVerb] & 0x1F);
            if (inReVerb != ZtZeroTierVerb.Hello)
            {
                continue;
            }

            var inRePacketId = BinaryPrimitives.ReadUInt64BigEndian(packetBytes.AsSpan(OkIndexInRePacketId, 8));
            if (inRePacketId != packetId)
            {
                continue;
            }

            return;
        }
    }

    public static async Task<ZtZeroTierHelloOk> HelloRootsAsync(
        ZtZeroTierIdentity localIdentity,
        ZtZeroTierWorld planet,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var udp = new ZtZeroTierUdpTransport(localPort: 0, enableIpv6: true);
        try
        {
            return await HelloRootsAsync(udp, localIdentity, planet, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await udp.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static byte[] BuildHelloPacket(
        ZtZeroTierIdentity localIdentity,
        ZtNodeId destination,
        IPEndPoint physicalDestination,
        ZtZeroTierWorld planet,
        ulong timestamp,
        ReadOnlySpan<byte> sharedKey,
        out ulong packetId)
    {
        var iv = new byte[8];
        RandomNumberGenerator.Fill(iv);
        packetId = BinaryPrimitives.ReadUInt64BigEndian(iv);

        var identityLength = ZtZeroTierIdentityCodec.GetSerializedLength(localIdentity, includePrivate: false);
        var inetLength = ZtZeroTierInetAddressCodec.GetSerializedLength(physicalDestination);

        var payloadFixedLength =
            1 + // protocol version
            1 + // major
            1 + // minor
            2 + // revision
            8 + // timestamp
            identityLength +
            inetLength +
            8 + // planet world id
            8; // planet timestamp

        var startCryptedPortionAtPayloadOffset = payloadFixedLength;

        var moonListLength = 2; // u16 count (0)
        var payload = new byte[payloadFixedLength + moonListLength];

        var p = 0;
        payload[p++] = AdvertisedProtocolVersion;
        payload[p++] = AdvertisedMajorVersion;
        payload[p++] = AdvertisedMinorVersion;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(p, 2), AdvertisedRevision);
        p += 2;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(p, 8), timestamp);
        p += 8;

        p += ZtZeroTierIdentityCodec.Serialize(localIdentity, payload.AsSpan(p), includePrivate: false);
        p += ZtZeroTierInetAddressCodec.Serialize(physicalDestination, payload.AsSpan(p));

        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(p, 8), planet.Id);
        p += 8;
        BinaryPrimitives.WriteUInt64BigEndian(payload.AsSpan(p, 8), planet.Timestamp);
        p += 8;

        // Encrypted portion: just moon count (0) for MVP.
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(p, 2), 0);
        p += 2;

        if (p != payload.Length)
        {
            throw new InvalidOperationException("HELLO payload size mismatch.");
        }

        var header = new ZtZeroTierPacketHeader(
            PacketId: packetId,
            Destination: destination,
            Source: localIdentity.NodeId,
            Flags: 0,
            Mac: 0,
            VerbRaw: (byte)ZtZeroTierVerb.Hello);

        var packet = ZtZeroTierPacketCodec.Encode(header, payload);

        // cryptField() encrypts the remainder of HELLO (moon list), using the raw key and IV with low 3 bits masked off.
        CryptHelloRemainder(packet, sharedKey, ZtZeroTierPacketHeader.Length + startCryptedPortionAtPayloadOffset);

        // HELLO is not fully encrypted, but must be MACed.
        ZtZeroTierPacketCrypto.Armor(packet, sharedKey, encryptPayload: false);

        return packet;
    }

    private static void CryptHelloRemainder(byte[] packet, ReadOnlySpan<byte> key, int start)
    {
        if (key.Length < 32)
        {
            throw new ArgumentException("Key must be at least 32 bytes.", nameof(key));
        }

        if ((uint)start > (uint)packet.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        var length = packet.Length - start;
        if (length == 0)
        {
            return;
        }

        Span<byte> iv = stackalloc byte[8];
        packet.AsSpan(0, 8).CopyTo(iv);
        iv[7] &= 0xF8;

        var keystream = new byte[length];
        ZtZeroTierSalsa20.GenerateKeyStream12(key.Slice(0, 32), iv, keystream);
        for (var i = 0; i < length; i++)
        {
            packet[start + i] ^= keystream[i];
        }
    }
}
