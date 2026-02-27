using Microsoft.Extensions.Logging;
using ZTSharp.Transport;
using System.Diagnostics.CodeAnalysis;

namespace ZTSharp.Internal;

internal sealed class NodeLifecycleService : IAsyncDisposable
{
    private readonly NodeRuntimeState _runtime;
    [SuppressMessage(
        "Reliability",
        "CA2213:Disposable fields should be disposed",
        Justification = "DisposeAsync must be safe during concurrent lifecycle operations; disposing this lock can throw on in-flight Release calls.")]
    private readonly SemaphoreSlim _stateLock;
    private readonly CancellationTokenSource _nodeCts;
    private readonly INodeTransport _transport;
    private readonly IStateStore _store;
    private readonly ILogger _logger;
    private readonly NodeEventStream _events;
    private readonly NodeIdentityService _identityService;
    private readonly NodeNetworkService _networkService;
    private readonly NodeTransportService _transportService;
    private readonly bool _ownsTransport;

    private int _disposeState;

    public NodeLifecycleService(
        NodeRuntimeState runtime,
        SemaphoreSlim stateLock,
        CancellationTokenSource nodeCts,
        INodeTransport transport,
        IStateStore store,
        ILogger logger,
        NodeEventStream events,
        NodeIdentityService identityService,
        NodeNetworkService networkService,
        NodeTransportService transportService,
        bool ownsTransport)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _stateLock = stateLock ?? throw new ArgumentNullException(nameof(stateLock));
        _nodeCts = nodeCts ?? throw new ArgumentNullException(nameof(nodeCts));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _identityService = identityService ?? throw new ArgumentNullException(nameof(identityService));
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _transportService = transportService ?? throw new ArgumentNullException(nameof(transportService));
        _ownsTransport = ownsTransport;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_runtime.State is NodeState.Running or NodeState.Starting)
            {
                return;
            }

            if (_runtime.State is NodeState.Stopping)
            {
                throw new InvalidOperationException("Cannot start while stopping.");
            }

            _runtime.State = NodeState.Starting;
            _events.Publish(EventCode.NodeStarting, DateTimeOffset.UtcNow);

            var identity = await _identityService.EnsureIdentityAsync(cancellationToken).ConfigureAwait(false);
            _runtime.NodeId = identity.NodeId;

            await _networkService
                .RecoverNetworksAsync(
                    _runtime.NodeId.Value,
                    _transportService.GetLocalTransportEndpoint(),
                    _transportService.OnFrameReceivedAsync,
                    cancellationToken)
                .ConfigureAwait(false);

            _runtime.State = NodeState.Running;
            _events.Publish(EventCode.NodeStarted, DateTimeOffset.UtcNow, message: "Node started");
        }
        catch (OperationCanceledException)
        {
            _runtime.State = NodeState.Faulted;
            throw;
        }
        catch (Exception ex)
        {
            _runtime.State = NodeState.Faulted;
#pragma warning disable CA1848
            _logger.LogError(ex, "Failed to start node");
#pragma warning restore CA1848
            _events.Publish(EventCode.NodeFaulted, DateTimeOffset.UtcNow, message: ex.Message, error: ex);
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_runtime.State is NodeState.Stopped or NodeState.Faulted)
            {
                return;
            }

            _runtime.State = NodeState.Stopping;
            _events.Publish(EventCode.NodeStopping, DateTimeOffset.UtcNow);

            await _networkService.UnregisterAllNetworksAsync(cancellationToken).ConfigureAwait(false);
            await _transport.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _store.FlushAsync(cancellationToken).ConfigureAwait(false);
            _runtime.State = NodeState.Stopped;
            _events.Publish(EventCode.NodeStopped, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            _runtime.State = NodeState.Faulted;
            throw;
        }
        catch (Exception ex)
        {
            _runtime.State = NodeState.Faulted;
#pragma warning disable CA1848
            _logger.LogError(ex, "Failed to stop node");
#pragma warning restore CA1848
            _events.Publish(EventCode.NodeFaulted, DateTimeOffset.UtcNow, message: ex.Message, error: ex);
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task EnsureRunningAsync(CancellationToken cancellationToken)
    {
        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_runtime.State != NodeState.Running)
            {
                throw new InvalidOperationException("Node must be started.");
            }
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task ExecuteWhileRunningAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_runtime.State != NodeState.Running)
            {
                throw new InvalidOperationException("Node must be started.");
            }

            await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<T> ExecuteWhileRunningAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_runtime.State != NodeState.Running)
            {
                throw new InvalidOperationException("Node must be started.");
            }

            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        try
        {
            // Best-effort: block new operations promptly, but don't wedge forever if some operation is stuck.
            await _stateLock.WaitAsync(shutdownCts.Token).ConfigureAwait(false);
            try
            {
                if (_runtime.Disposed)
                {
                    return;
                }

                _runtime.Disposed = true;
            }
            finally
            {
                _stateLock.Release();
            }
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
        {
            // Worst case: a concurrent lifecycle operation is wedged under the state lock. Mark the node as disposed
            // anyway so future operations fail fast.
            _runtime.Disposed = true;
        }

        try
        {
            await _nodeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            await _stateLock.WaitAsync(shutdownCts.Token).ConfigureAwait(false);
            try
            {
                if (_runtime.State is not NodeState.Stopped and not NodeState.Faulted)
                {
                    _runtime.State = NodeState.Stopping;
                    _events.Publish(EventCode.NodeStopping, DateTimeOffset.UtcNow);

                    await _networkService.UnregisterAllNetworksAsync(shutdownCts.Token).ConfigureAwait(false);
                    await _transport.FlushAsync(shutdownCts.Token).ConfigureAwait(false);
                    await _store.FlushAsync(shutdownCts.Token).ConfigureAwait(false);
                    _runtime.State = NodeState.Stopped;
                    _events.Publish(EventCode.NodeStopped, DateTimeOffset.UtcNow);
                }
            }
            finally
            {
                _stateLock.Release();
            }
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // Dispose must be best-effort.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _runtime.State = NodeState.Faulted;
#pragma warning disable CA1848
            _logger.LogError(ex, "Failed to stop node during disposal");
#pragma warning restore CA1848
            _events.Publish(EventCode.NodeFaulted, DateTimeOffset.UtcNow, message: ex.Message, error: ex);
        }

        try
        {
            await _networkService.LeaveAllNetworksAsync(shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (_ownsTransport && _transport is IAsyncDisposable asyncTransport)
        {
            using var transportDisposeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await asyncTransport
                    .DisposeAsync()
                    .AsTask()
                    .WaitAsync(transportDisposeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (transportDisposeCts.IsCancellationRequested)
            {
            }
        }

        _events.Complete();
        _nodeCts.Dispose();
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_runtime.Disposed || Volatile.Read(ref _disposeState) != 0, nameof(Node));
    }
}
