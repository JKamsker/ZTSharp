using System.IO;
using System.Net;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpReceiveLoop
{
    private readonly IUserSpaceIpLink _link;
    private readonly IPAddress _localAddress;
    private readonly IPAddress _remoteAddress;
    private readonly ushort _remotePort;
    private readonly ushort _localPort;
    private readonly UserSpaceTcpSender _sender;
    private readonly UserSpaceTcpReceiver _receiver;
    private readonly UserSpaceTcpConnectionSignals _signals;

    public UserSpaceTcpReceiveLoop(
        IUserSpaceIpLink link,
        IPAddress localAddress,
        IPAddress remoteAddress,
        ushort localPort,
        ushort remotePort,
        UserSpaceTcpSender sender,
        UserSpaceTcpReceiver receiver,
        UserSpaceTcpConnectionSignals signals)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(localAddress);
        ArgumentNullException.ThrowIfNull(remoteAddress);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(signals);

        _link = link;
        _localAddress = localAddress;
        _remoteAddress = remoteAddress;
        _localPort = localPort;
        _remotePort = remotePort;
        _sender = sender;
        _receiver = receiver;
        _signals = signals;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            ReadOnlyMemory<byte> ipPacket;
            try
            {
                ipPacket = await _link.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                Close(ex);
                return;
            }

            if (!TryParseAndFilterTcpPacket(ipPacket, out var seq, out var ack, out var flags, out var windowSize, out var tcpPayload))
            {
                continue;
            }

            _sender.UpdateRemoteSendWindow(windowSize);

            if ((flags & TcpCodec.Flags.Rst) != 0)
            {
                Close(new IOException("Remote reset the connection."));
                continue;
            }

            if (!_signals.Connected)
            {
                if ((flags & (TcpCodec.Flags.Syn | TcpCodec.Flags.Ack)) != (TcpCodec.Flags.Syn | TcpCodec.Flags.Ack))
                {
                    continue;
                }

                if (ack != _sender.SendNext)
                {
                    continue;
                }

                _receiver.Initialize(unchecked(seq + 1));

                await _sender.SendPureAckAsync(_receiver.RecvNext, cancellationToken).ConfigureAwait(false);

                _signals.Connected = true;
                _signals.ConnectTcs?.TrySetResult(true);
                continue;
            }

            if ((flags & TcpCodec.Flags.Ack) != 0)
            {
                _sender.OnAckReceived(ack);
            }

            var hasFin = (flags & TcpCodec.Flags.Fin) != 0;
            var segmentResult = await _receiver.ProcessSegmentAsync(seq, tcpPayload, hasFin).ConfigureAwait(false);

            if (segmentResult.ClosedNow)
            {
                _sender.FailPendingOperations(new IOException("Remote has closed the connection."));
            }

            if (segmentResult.ShouldAck)
            {
                await _sender.SendPureAckAsync(_receiver.RecvNext, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Close(Exception exception)
    {
        _receiver.MarkRemoteClosed(exception);
        _signals.ConnectTcs?.TrySetException(exception);
        _sender.FailPendingOperations(exception);
    }

    private bool TryParseAndFilterTcpPacket(
        ReadOnlyMemory<byte> ipPacket,
        out uint seq,
        out uint ack,
        out TcpCodec.Flags flags,
        out ushort windowSize,
        out ReadOnlySpan<byte> tcpPayload)
    {
        seq = 0;
        ack = 0;
        flags = 0;
        windowSize = 0;
        tcpPayload = ReadOnlySpan<byte>.Empty;

        IPAddress src;
        IPAddress dst;
        ReadOnlySpan<byte> ipPayload;
        if (_localAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (!Ipv4Codec.TryParse(ipPacket.Span, out src, out dst, out var protocol, out ipPayload))
            {
                return false;
            }

            if (!src.Equals(_remoteAddress) || !dst.Equals(_localAddress) || protocol != TcpCodec.ProtocolNumber)
            {
                return false;
            }
        }
        else
        {
            if (!Ipv6Codec.TryParse(ipPacket.Span, out src, out dst, out var nextHeader, out _, out ipPayload))
            {
                return false;
            }

            if (!src.Equals(_remoteAddress) || !dst.Equals(_localAddress) || nextHeader != TcpCodec.ProtocolNumber)
            {
                return false;
            }
        }

        if (!TcpCodec.TryParseWithChecksum(_remoteAddress, _localAddress, ipPayload, out var srcPort, out var dstPort, out seq, out ack, out flags, out windowSize, out tcpPayload))
        {
            return false;
        }

        if (srcPort != _remotePort || dstPort != _localPort)
        {
            return false;
        }

        return true;
    }
}
