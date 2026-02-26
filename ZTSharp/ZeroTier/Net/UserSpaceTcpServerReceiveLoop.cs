using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace ZTSharp.ZeroTier.Net;

internal sealed class UserSpaceTcpServerReceiveLoop
{
    private readonly IUserSpaceIpLink _link;
    private readonly IPAddress _localAddress;
    private readonly IPAddress _remoteAddress;
    private readonly ushort _localPort;
    private readonly ushort _remotePort;
    private readonly ushort _mss;
    private readonly UserSpaceTcpSender _sender;
    private readonly UserSpaceTcpReceiver _receiver;
    private readonly UserSpaceTcpAcceptSignals _signals;

    private bool _handshakeStarted;
    private uint _synAckSeq;

    public UserSpaceTcpServerReceiveLoop(
        IUserSpaceIpLink link,
        IPAddress localAddress,
        ushort localPort,
        IPAddress remoteAddress,
        ushort remotePort,
        ushort mss,
        UserSpaceTcpSender sender,
        UserSpaceTcpReceiver receiver,
        UserSpaceTcpAcceptSignals signals)
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
        _mss = mss;
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

            uint seq = 0;
            uint ack = 0;
            TcpCodec.Flags flags = 0;
            ushort windowSize = 0;

            var sendSynAck = false;
            ReadOnlyMemory<byte> synAckOptions = default;

            var sendAck = false;
            var ackToSend = 0u;

            var parsed = false;
            {
                if (!TryParseAndFilterTcpPacket(ipPacket, out seq, out ack, out flags, out windowSize, out var tcpOptions, out var tcpPayload))
                {
                    parsed = false;
                }
                else
                {
                    parsed = true;

                    _sender.UpdateRemoteSendWindow(windowSize);

                    if ((flags & TcpCodec.Flags.Rst) != 0)
                    {
                        Close(new IOException("Remote reset the connection."));
                        continue;
                    }

                    if (!_signals.Connected)
                    {
                        if ((flags & TcpCodec.Flags.Syn) != 0 && (flags & TcpCodec.Flags.Ack) == 0)
                        {
                            if (!_handshakeStarted)
                            {
                                _handshakeStarted = true;
                                _signals.AcceptTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                                var iss = GenerateInitialSequenceNumber();
                                _sender.InitializeSendState(iss);
                                _synAckSeq = _sender.AllocateNextSequence(bytes: 1);
                            }

                            if (TcpCodec.TryGetMssOption(tcpOptions, out var remoteMss))
                            {
                                _sender.UpdateEffectiveMss(remoteMss);
                            }

                            _receiver.Initialize(unchecked(seq + 1));

                            synAckOptions = TcpCodec.EncodeMssOption(_mss);
                            sendSynAck = true;
                        }
                        else if (_handshakeStarted &&
                                 (flags & TcpCodec.Flags.Ack) != 0 &&
                                 ack == _sender.SendNext)
                        {
                            _signals.Connected = true;
                            _signals.AcceptTcs?.TrySetResult(true);
                        }
                    }

                    if (_signals.Connected && !sendSynAck)
                    {
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
                            sendAck = true;
                            ackToSend = _receiver.RecvNext;
                        }
                    }
                }
            }

            if (!parsed)
            {
                continue;
            }

            if (sendSynAck)
            {
                await _sender
                    .SendSegmentAsync(
                        seq: _synAckSeq,
                        ack: _receiver.RecvNext,
                        flags: TcpCodec.Flags.Syn | TcpCodec.Flags.Ack,
                        options: synAckOptions,
                        payload: ReadOnlyMemory<byte>.Empty,
                        cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (sendAck)
            {
                await _sender.SendPureAckAsync(ackToSend, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Close(Exception exception)
    {
        _receiver.MarkRemoteClosed(exception);
        _signals.AcceptTcs?.TrySetException(exception);
        _sender.FailPendingOperations(exception);
    }

    private bool TryParseAndFilterTcpPacket(
        ReadOnlyMemory<byte> ipPacket,
        out uint seq,
        out uint ack,
        out TcpCodec.Flags flags,
        out ushort windowSize,
        out ReadOnlySpan<byte> tcpOptions,
        out ReadOnlySpan<byte> tcpPayload)
    {
        seq = 0;
        ack = 0;
        flags = 0;
        windowSize = 0;
        tcpOptions = ReadOnlySpan<byte>.Empty;
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

        if (!TcpCodec.TryParseWithChecksum(_remoteAddress, _localAddress, ipPayload, out var srcPort, out var dstPort, out seq, out ack, out flags, out windowSize, out tcpOptions, out tcpPayload))
        {
            return false;
        }

        if (srcPort != _remotePort || dstPort != _localPort)
        {
            return false;
        }

        return true;
    }

    private static uint GenerateInitialSequenceNumber()
    {
        Span<byte> buffer = stackalloc byte[4];
        RandomNumberGenerator.Fill(buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }
}
