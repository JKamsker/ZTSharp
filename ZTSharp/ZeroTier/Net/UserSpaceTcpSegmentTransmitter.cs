using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpSegmentTransmitter
{
    private readonly IUserSpaceIpLink _link;
    private readonly IPAddress _localAddress;
    private readonly IPAddress _remoteAddress;
    private readonly ushort _localPort;
    private readonly ushort _remotePort;

    public UserSpaceTcpSegmentTransmitter(
        IUserSpaceIpLink link,
        IPAddress localAddress,
        IPAddress remoteAddress,
        ushort localPort,
        ushort remotePort)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);

        _link = link;
        _localAddress = localAddress;
        _remoteAddress = remoteAddress;
        _localPort = localPort;
        _remotePort = remotePort;
    }

    public Task SendAsync(
        uint seq,
        uint ack,
        TcpCodec.Flags flags,
        ushort windowSize,
        ReadOnlyMemory<byte> options,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var tcp = TcpCodec.Encode(
            _localAddress,
            _remoteAddress,
            sourcePort: _localPort,
            destinationPort: _remotePort,
            sequenceNumber: seq,
            acknowledgmentNumber: ack,
            flags,
            windowSize: windowSize,
            options: options.Span,
            payload.Span);

        byte[] ip;
        if (_localAddress.AddressFamily == AddressFamily.InterNetwork)
        {
            ip = Ipv4Codec.Encode(
                _localAddress,
                _remoteAddress,
                protocol: TcpCodec.ProtocolNumber,
                payload: tcp,
                identification: GenerateIpIdentification());
        }
        else
        {
            ip = Ipv6Codec.Encode(
                _localAddress,
                _remoteAddress,
                nextHeader: TcpCodec.ProtocolNumber,
                payload: tcp,
                hopLimit: 64);
        }

        return _link.SendAsync(ip, cancellationToken).AsTask();
    }

    private static ushort GenerateIpIdentification()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
    }
}

