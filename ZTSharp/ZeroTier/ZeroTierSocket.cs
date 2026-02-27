using System.Net;
using ZTSharp.ZeroTier.Http;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier;

[global::System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Usage",
    "CA2213:Disposable fields should be disposed",
    Justification = "Disposal must not wedge behind in-flight operations; CTS/SemaphoreSlim instances are not required to be disposed for correctness in this type.")]
public sealed class ZeroTierSocket : IAsyncDisposable
{
    private readonly ZeroTierSocketOptions _options;
    private readonly string _statePath;
    private readonly ZeroTierIdentity _identity;
    private readonly ZeroTierWorld _planet;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _joinLock = new(1, 1);
    private readonly SemaphoreSlim _runtimeLock = new(1, 1);
    private ZeroTierDataplaneRuntime? _runtime;
    private byte[]? _networkConfigDictionaryBytes;
    private ZeroTierHelloOk? _upstreamRoot;
    private byte[]? _upstreamRootKey;
    private Task? _joinTask;
    private Task<ZeroTierDataplaneRuntime>? _runtimeTask;
    private bool _joined;
    private int _disposeState;

    internal ZeroTierSocket(ZeroTierSocketOptions options, string statePath, ZeroTierIdentity identity, ZeroTierWorld planet)
    {
        _options = options;
        _statePath = statePath;
        _identity = identity;
        _planet = planet;
        NodeId = identity.NodeId;
        ManagedIps = ZeroTierSocketStatePersistence.LoadManagedIps(statePath, options.NetworkId);
        _networkConfigDictionaryBytes = ZeroTierSocketStatePersistence.LoadNetworkConfigDictionary(statePath, options.NetworkId);
    }

    public NodeId NodeId { get; private set; }

    public IReadOnlyList<IPAddress> ManagedIps { get; private set; } = Array.Empty<IPAddress>();

    public static Task<ZeroTierSocket> CreateAsync(
        ZeroTierSocketOptions options,
        CancellationToken cancellationToken = default)
        => ZeroTierSocketFactory.CreateAsync(options, cancellationToken);

    public async Task JoinAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linkedCts.Token;

        Task joinTask;
        try
        {
            await _joinLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ZeroTierSocket));
        }
        try
        {
            ThrowIfDisposed();
            if (_joined)
            {
                return;
            }

            if (_joinTask is { IsCompleted: true } && !_joined)
            {
                _joinTask = null;
            }

            _joinTask ??= JoinCoreAsync(_shutdown.Token);
            joinTask = _joinTask;
        }
        finally
        {
            _joinLock.Release();
        }

        try
        {
            await joinTask.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ZeroTierSocket));
        }
    }

    private async Task JoinCoreAsync(CancellationToken cancellationToken)
    {
        var result = await ZeroTierNetworkConfigClient
            .FetchAsync(
                _identity,
                _planet,
                _options.NetworkId,
                _options.JoinTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await _joinLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_joined)
            {
                return;
            }

            ManagedIps = result.ManagedIps;
            _networkConfigDictionaryBytes = result.DictionaryBytes;
            _upstreamRoot = result.UpstreamRoot;
            _upstreamRootKey = result.UpstreamRootKey;
            ZeroTierSocketStatePersistence.PersistNetworkState(_statePath, _options.NetworkId, result.DictionaryBytes, result.ManagedIps);
            _joined = true;
        }
        finally
        {
            _joinLock.Release();
        }
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Handler ownership transfers to HttpClient, which is disposed by the caller.")]
    public HttpClient CreateHttpClient(Uri? baseAddress = null)
    {
        ThrowIfDisposed();

        var handler = new ZeroTierHttpMessageHandler(this);
        var client = new HttpClient(handler, disposeHandler: true);
        if (baseAddress is not null)
        {
            client.BaseAddress = baseAddress;
        }

        return client;
    }

    public Sockets.ManagedSocket CreateSocket(
        System.Net.Sockets.AddressFamily addressFamily,
        System.Net.Sockets.SocketType socketType,
        System.Net.Sockets.ProtocolType protocolType)
    {
        ThrowIfDisposed();
        return new Sockets.ManagedSocket(this, addressFamily, socketType, protocolType);
    }

    public async ValueTask<ZeroTierTcpListener> ListenTcpAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await JoinAsync(cancellationToken).ConfigureAwait(false);
        return await ListenTcpAsync(GetDefaultLocalManagedAddressOrThrow(), port, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ZeroTierTcpListener> ListenTcpAsync(IPAddress localAddress, int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        return await ZeroTierSocketBindings.ListenTcpAsync(
                ensureJoinedAsync: JoinAsync,
                getManagedIps: () => ManagedIps,
                getInlineCom: GetInlineComOrThrow,
                getOrCreateRuntimeAsync: GetOrCreateRuntimeAsync,
                localAddress,
                port,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal ValueTask<ZeroTierTcpListener> ListenTcpAsync(
        IPAddress localAddress,
        int port,
        int acceptQueueCapacity,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        return ZeroTierSocketBindings.ListenTcpAsync(
            ensureJoinedAsync: JoinAsync,
            getManagedIps: () => ManagedIps,
            getInlineCom: GetInlineComOrThrow,
            getOrCreateRuntimeAsync: GetOrCreateRuntimeAsync,
            localAddress,
            port,
            cancellationToken,
            acceptQueueCapacity: acceptQueueCapacity);
    }

    public async ValueTask<ZeroTierUdpSocket> BindUdpAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        await JoinAsync(cancellationToken).ConfigureAwait(false);
        return await BindUdpAsync(GetDefaultLocalManagedAddressOrThrow(), port, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ZeroTierUdpSocket> BindUdpAsync(
        IPAddress localAddress,
        int port,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        return await ZeroTierSocketBindings.BindUdpAsync(
                ensureJoinedAsync: JoinAsync,
                getManagedIps: () => ManagedIps,
                getInlineCom: GetInlineComOrThrow,
                getOrCreateRuntimeAsync: GetOrCreateRuntimeAsync,
                localAddress,
                port,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask<Stream> ConnectTcpAsync(IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfDisposed();
        return ConnectTcpCoreAsync(local: null, remote, cancellationToken);
    }

    public ValueTask<Stream> ConnectTcpAsync(IPEndPoint local, IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();

        ThrowIfDisposed();
        return ConnectTcpCoreAsync(local, remote, cancellationToken);
    }

    public async ValueTask<Stream> ConnectTcpAsync(IPEndPoint remote, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await ZeroTierTimeouts.RunWithTimeoutAsync(
                timeout,
                operation: "TCP connect",
                action: ct => ConnectTcpAsync(remote, ct),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<Stream> ConnectTcpAsync(IPEndPoint local, IPEndPoint remote, TimeSpan timeout, CancellationToken cancellationToken = default)
        => await ZeroTierTimeouts.RunWithTimeoutAsync(
                timeout,
                operation: "TCP connect",
                action: ct => ConnectTcpAsync(local, remote, ct),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        _runtimeTask = null;
        var runtime = Interlocked.Exchange(ref _runtime, null);

        if (runtime is not null)
        {
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<Stream> ConnectTcpCoreAsync(IPEndPoint? local, IPEndPoint remote, CancellationToken cancellationToken)
    {
        var (stream, _) = await ConnectTcpCoreWithLocalEndpointAsync(local, remote, cancellationToken).ConfigureAwait(false);
        return stream;
    }

    internal ValueTask<(Stream Stream, IPEndPoint LocalEndpoint)> ConnectTcpWithLocalEndpointAsync(
        IPEndPoint remote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        return ConnectTcpCoreWithLocalEndpointAsync(local: null, remote, cancellationToken);
    }

    internal ValueTask<(Stream Stream, IPEndPoint LocalEndpoint)> ConnectTcpWithLocalEndpointAsync(
        IPEndPoint local,
        IPEndPoint remote,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        return ConnectTcpCoreWithLocalEndpointAsync(local, remote, cancellationToken);
    }

    private async ValueTask<(Stream Stream, IPEndPoint LocalEndpoint)> ConnectTcpCoreWithLocalEndpointAsync(
        IPEndPoint? local,
        IPEndPoint remote,
        CancellationToken cancellationToken)
    {
        return await ZeroTierSocketTcpConnector.ConnectWithLocalEndpointAsync(
                ensureJoinedAsync: JoinAsync,
                getManagedIps: () => ManagedIps,
                getInlineCom: GetInlineComOrThrow,
                getOrCreateRuntimeAsync: GetOrCreateRuntimeAsync,
                local,
                remote,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private byte[] GetInlineComOrThrow()
    {
        var dict = _networkConfigDictionaryBytes ?? throw new InvalidOperationException("Missing network config dictionary (join not completed?).");
        return ZeroTierInlineCom.GetInlineCom(dict);
    }

    private IPAddress GetDefaultLocalManagedAddressOrThrow()
        => ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ??
           ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) ??
           throw new InvalidOperationException("No managed IP assigned for this network.");

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "UDP transport ownership transfers to ZeroTierDataplaneRuntime, which is disposed by ZeroTierSocket.DisposeAsync.")]
    private async Task<ZeroTierDataplaneRuntime> GetOrCreateRuntimeAsync(
        byte[] inlineCom,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var token = linkedCts.Token;

        Task<ZeroTierDataplaneRuntime> runtimeTask;
        try
        {
            await _runtimeLock.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ZeroTierSocket));
        }
        try
        {
            ThrowIfDisposed();
            if (_runtime is not null)
            {
                return _runtime;
            }

            if (_runtimeTask is { IsCompleted: true })
            {
                _runtimeTask = null;
            }

            _runtimeTask ??= CreateRuntimeAsync(inlineCom, _shutdown.Token);
            runtimeTask = _runtimeTask;
        }
        finally
        {
            _runtimeLock.Release();
        }

        try
        {
            return await runtimeTask.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(ZeroTierSocket));
        }
    }

    private async Task<ZeroTierDataplaneRuntime> CreateRuntimeAsync(byte[] inlineCom, CancellationToken cancellationToken)
    {
        var (created, helloOk, rootKey) = await ZeroTierSocketRuntimeBootstrapper
            .CreateAsync(
                multipath: _options.Multipath,
                localIdentity: _identity,
                planet: _planet,
                networkId: _options.NetworkId,
                managedIps: ManagedIps,
                inlineCom: inlineCom,
                cachedRoot: _upstreamRoot,
                cachedRootKey: _upstreamRootKey,
                cancellationToken)
            .ConfigureAwait(false);

        var createdNeedsDispose = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();

            ZeroTierDataplaneRuntime? toDispose = null;
            ZeroTierDataplaneRuntime runtime;
            await _runtimeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (_runtime is not null)
                {
                    toDispose = created;
                    runtime = _runtime;
                }
                else
                {
                    _upstreamRoot ??= helloOk;
                    _upstreamRootKey ??= rootKey;
                    _runtime = created;
                    runtime = created;
                    createdNeedsDispose = false;
                }
            }
            finally
            {
                _runtimeLock.Release();
            }

            if (toDispose is not null)
            {
                await toDispose.DisposeAsync().ConfigureAwait(false);
                createdNeedsDispose = false;
            }

            return runtime;
        }
        catch
        {
            if (createdNeedsDispose)
            {
                await created.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);
}
