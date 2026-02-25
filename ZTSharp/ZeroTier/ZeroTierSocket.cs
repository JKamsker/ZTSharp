using System.Net;
using ZTSharp.ZeroTier.Http;
using ZTSharp.ZeroTier.Internal;
using ZTSharp.ZeroTier.Net;
using ZTSharp.ZeroTier.Protocol;
using ZTSharp.ZeroTier.Transport;

namespace ZTSharp.ZeroTier;

public sealed class ZeroTierSocket : IAsyncDisposable
{
    private readonly ZeroTierSocketOptions _options;
    private readonly string _statePath;
    private readonly ZeroTierIdentity _identity;
    private readonly ZeroTierWorld _planet;
    private readonly SemaphoreSlim _joinLock = new(1, 1);
    private readonly SemaphoreSlim _runtimeLock = new(1, 1);
    private ZeroTierDataplaneRuntime? _runtime;
    private byte[]? _networkConfigDictionaryBytes;
    private ZeroTierHelloOk? _upstreamRoot;
    private byte[]? _upstreamRootKey;
    private bool _joined;
    private bool _disposed;

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _joinLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_joined)
            {
                return;
            }

            var result = await ZeroTierNetworkConfigClient.FetchAsync(
                    _identity,
                    _planet,
                    _options.NetworkId,
                    _options.JoinTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

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
        => new(this, addressFamily, socketType, protocolType);

    public async ValueTask<ZeroTierTcpListener> ListenTcpAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        await JoinAsync(cancellationToken).ConfigureAwait(false);
        return await ListenTcpAsync(GetDefaultLocalManagedAddressOrThrow(), port, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ZeroTierTcpListener> ListenTcpAsync(IPAddress localAddress, int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        return await ZeroTierSocketBindings.ListenTcpAsync(
                ensureJoinedAsync: JoinAsync,
                getManagedIps: () => ManagedIps,
                getLocalManagedIpv4AndInlineCom: GetLocalManagedIpv4AndInlineCom,
                getOrCreateRuntimeAsync: GetOrCreateRuntimeAsync,
                localAddress,
                port,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask<ZeroTierUdpSocket> BindUdpAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        await JoinAsync(cancellationToken).ConfigureAwait(false);
        return await BindUdpAsync(GetDefaultLocalManagedAddressOrThrow(), port, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ZeroTierUdpSocket> BindUdpAsync(
        IPAddress localAddress,
        int port,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        return await ZeroTierSocketBindings.BindUdpAsync(
                ensureJoinedAsync: JoinAsync,
                getManagedIps: () => ManagedIps,
                getLocalManagedIpv4AndInlineCom: GetLocalManagedIpv4AndInlineCom,
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

        ObjectDisposedException.ThrowIf(_disposed, this);
        return ConnectTcpCoreAsync(local: null, remote, cancellationToken);
    }

    public ValueTask<Stream> ConnectTcpAsync(IPEndPoint local, IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();

        ObjectDisposedException.ThrowIf(_disposed, this);
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _runtimeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_runtime is not null)
            {
                await _runtime.DisposeAsync().ConfigureAwait(false);
                _runtime = null;
            }
        }
        finally
        {
            _runtimeLock.Release();
            _runtimeLock.Dispose();
            _joinLock.Dispose();
        }
    }

    private async ValueTask<Stream> ConnectTcpCoreAsync(IPEndPoint? local, IPEndPoint remote, CancellationToken cancellationToken)
    {
        return await ZeroTierSocketTcpConnector.ConnectAsync(
                ensureJoinedAsync: JoinAsync,
                getManagedIps: () => ManagedIps,
                getLocalManagedIpv4AndInlineCom: GetLocalManagedIpv4AndInlineCom,
                getOrCreateRuntimeAsync: GetOrCreateRuntimeAsync,
                local,
                remote,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private (IPAddress? LocalManagedIpV4, byte[] InlineCom) GetLocalManagedIpv4AndInlineCom()
    {
        var dict = _networkConfigDictionaryBytes ?? throw new InvalidOperationException("Missing network config dictionary (join not completed?).");
        return (ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), ZeroTierInlineCom.GetInlineCom(dict));
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
        IPAddress? localManagedIpV4,
        byte[] inlineCom,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _runtimeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime is not null)
            {
                return _runtime;
            }

            var (runtime, helloOk, rootKey) = await ZeroTierSocketRuntimeBootstrapper
                .CreateAsync(
                    localIdentity: _identity,
                    planet: _planet,
                    networkId: _options.NetworkId,
                    managedIps: ManagedIps,
                    localManagedIpV4: localManagedIpV4,
                    inlineCom: inlineCom,
                    cachedRoot: _upstreamRoot,
                    cachedRootKey: _upstreamRootKey,
                    cancellationToken)
                .ConfigureAwait(false);

            _upstreamRoot ??= helloOk;
            _upstreamRootKey ??= rootKey;

            _runtime = runtime;
            return runtime;
        }
        finally
        {
            _runtimeLock.Release();
        }
    }
}
