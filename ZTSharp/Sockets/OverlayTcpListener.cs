using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ZTSharp.Sockets;

/// <summary>
/// Managed stream listener built on top of the node transport (not OS TCP).
/// </summary>
public sealed class OverlayTcpListener : IAsyncDisposable
{
    private const int HeaderLength = OverlayTcpFrameCodec.HeaderLength;

    private readonly Channel<OverlayTcpClient> _acceptQueue;
    [SuppressMessage(
        "Reliability",
        "CA2213:Disposable fields should be disposed",
        Justification = "DisposeAsync must be idempotent; disposing this lock can throw on subsequent/overlapping DisposeAsync calls.")]
    private readonly SemaphoreSlim _disposeLock = new(1, 1);
    private readonly Node _node;
    private readonly ulong _networkId;
    private readonly int _localPort;

    private bool _disposed;

    public OverlayTcpListener(Node node, ulong networkId, int localPort)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (localPort is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }

        _node = node;
        _networkId = networkId;
        _localPort = localPort;
        _acceptQueue = Channel.CreateBounded<OverlayTcpClient>(new BoundedChannelOptions(capacity: 128)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            // DisposeAsync drains queued connections for best-effort cleanup, so we must allow an additional reader.
            SingleReader = false
        });

        _node.RawFrameReceived += OnFrameReceived;
    }

    public ValueTask<OverlayTcpClient> AcceptTcpClientAsync(CancellationToken cancellationToken = default)
        => _acceptQueue.Reader.ReadAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _disposeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _node.RawFrameReceived -= OnFrameReceived;
            _acceptQueue.Writer.TryComplete();
            while (_acceptQueue.Reader.TryRead(out var queued))
            {
                ObserveBestEffortAsync(queued.DisposeAsync().AsTask());
            }
        }
        finally
        {
            _disposeLock.Release();
        }
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership of accepted connections transfers to the accept queue consumer.")]
    private void OnFrameReceived(in RawFrame frame)
    {
        if (_disposed || frame.NetworkId != _networkId || frame.Payload.Length < HeaderLength)
        {
            return;
        }

        if (!OverlayTcpFrameCodec.TryParseHeader(frame.Payload.Span, out var type, out var sourcePort, out var destinationPort, out var destinationNodeId, out var connectionId))
        {
            return;
        }

        var localNodeId = _node.NodeId.Value;
        if (localNodeId == 0)
        {
            return;
        }

        if (type != OverlayTcpFrameCodec.FrameType.Syn ||
            destinationNodeId != localNodeId ||
            destinationPort != _localPort)
        {
            return;
        }

        var remoteNodeId = frame.SourceNodeId;
        var remotePort = sourcePort;

        OverlayTcpClient? accepted = null;
        try
        {
            accepted = new OverlayTcpClient(_node, _networkId, _localPort, remoteNodeId, remotePort, connectionId);
            if (!_acceptQueue.Writer.TryWrite(accepted))
            {
                return;
            }

            accepted = null;
        }
        finally
        {
            if (accepted is not null)
            {
                ObserveBestEffortAsync(accepted.DisposeAsync().AsTask());
            }
        }

        ObserveBestEffortAsync(SendSynAckAsync(remoteNodeId, remotePort, connectionId));
    }

    private async Task SendSynAckAsync(ulong remoteNodeId, int remotePort, ulong connectionId)
    {
        try
        {
            var usesPool = _node.LocalTransportEndpoint is not null;
            var frameLength = HeaderLength;
            var frame = usesPool ? ArrayPool<byte>.Shared.Rent(frameLength) : new byte[frameLength];
            try
            {
                OverlayTcpFrameCodec.BuildHeader(OverlayTcpFrameCodec.FrameType.SynAck, _localPort, remotePort, remoteNodeId, connectionId, frame.AsSpan(0, frameLength));
                await _node.SendFrameAsync(_networkId, frame.AsMemory(0, frameLength)).ConfigureAwait(false);
            }
            finally
            {
                if (usesPool)
                {
                    ArrayPool<byte>.Shared.Return(frame);
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or OperationCanceledException or SocketException)
        {
        }
    }

    private static void ObserveBestEffortAsync(Task task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = ObserveSlowAsync(task);
    }

    private static async Task ObserveSlowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or OperationCanceledException or SocketException)
        {
        }
    }
}
