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
        ArgumentNullException.ThrowIfNull(localAddress);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
        }

        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException($"Unsupported address family: {localAddress.AddressFamily}.");
        }

        await JoinAsync(cancellationToken).ConfigureAwait(false);
        if (!ManagedIps.Contains(localAddress))
        {
            throw new InvalidOperationException($"Local address '{localAddress}' is not one of this node's managed IPs.");
        }

        var (localManagedIpV4, comBytes) = GetLocalManagedIpv4AndInlineCom();
        var runtime = await GetOrCreateRuntimeAsync(localManagedIpV4, comBytes, cancellationToken).ConfigureAwait(false);
        return new ZeroTierTcpListener(runtime, localAddress, (ushort)port);
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
        ArgumentNullException.ThrowIfNull(localAddress);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (port is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");
        }

        if (localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            localAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException($"Unsupported address family: {localAddress.AddressFamily}.");
        }

        await JoinAsync(cancellationToken).ConfigureAwait(false);
        if (!ManagedIps.Contains(localAddress))
        {
            throw new InvalidOperationException($"Local address '{localAddress}' is not one of this node's managed IPs.");
        }

        var (localManagedIpV4, comBytes) = GetLocalManagedIpv4AndInlineCom();
        var runtime = await GetOrCreateRuntimeAsync(localManagedIpV4, comBytes, cancellationToken).ConfigureAwait(false);

        if (port != 0)
        {
            return new ZeroTierUdpSocket(runtime, localAddress, (ushort)port);
        }

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var localPort = ZeroTierEphemeralPorts.Generate();
            try
            {
                return new ZeroTierUdpSocket(runtime, localAddress, localPort);
            }
            catch (InvalidOperationException)
            {
            }
        }

        throw new InvalidOperationException("Failed to bind UDP to an ephemeral port (too many collisions).");
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
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be greater than zero.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await ConnectTcpAsync(remote, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP connect timed out after {timeout}.");
        }
    }

    public async ValueTask<Stream> ConnectTcpAsync(IPEndPoint local, IPEndPoint remote, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Timeout must be greater than zero.");
        }

        ObjectDisposedException.ThrowIf(_disposed, this);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            return await ConnectTcpAsync(local, remote, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP connect timed out after {timeout}.");
        }
    }

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

            var udp = new ZeroTierUdpTransport(localPort: 0, enableIpv6: true);
            try
            {
                var localManagedIpsV6 = ManagedIps
                    .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .ToArray();

                var (runtime, helloOk, rootKey) = await ZeroTierDataplaneRuntimeFactory
                    .CreateAsync(
                        udp,
                        localIdentity: _identity,
                        planet: _planet,
                        networkId: _options.NetworkId,
                        localManagedIpV4: localManagedIpV4,
                        localManagedIpsV6: localManagedIpsV6,
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
            catch
            {
                await udp.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _runtimeLock.Release();
        }
    }
}
