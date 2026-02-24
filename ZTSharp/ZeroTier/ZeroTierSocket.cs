using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
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

    private ZeroTierSocket(ZeroTierSocketOptions options, string statePath, ZeroTierIdentity identity, ZeroTierWorld planet)
    {
        _options = options;
        _statePath = statePath;
        _identity = identity;
        _planet = planet;
        NodeId = identity.NodeId;
        ManagedIps = LoadPersistedManagedIps(statePath, options.NetworkId);
        _networkConfigDictionaryBytes = LoadPersistedNetworkConfigDictionary(statePath, options.NetworkId);
    }

    public NodeId NodeId { get; private set; }

    public IReadOnlyList<IPAddress> ManagedIps { get; private set; } = Array.Empty<IPAddress>();

    public static Task<ZeroTierSocket> CreateAsync(
        ZeroTierSocketOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        ArgumentException.ThrowIfNullOrWhiteSpace(options.StateRootPath);
        ArgumentOutOfRangeException.ThrowIfZero(options.NetworkId);
        if (options.JoinTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "JoinTimeout must be positive.");
        }

        if (options.PlanetSource == ZeroTierPlanetSource.FilePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PlanetFilePath);
            if (!File.Exists(options.PlanetFilePath))
            {
                throw new FileNotFoundException("Planet file not found.", options.PlanetFilePath);
            }
        }

        if (options.PlanetSource != ZeroTierPlanetSource.EmbeddedDefault &&
            options.PlanetSource != ZeroTierPlanetSource.FilePath)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid PlanetSource value.");
        }

        var statePath = Path.Combine(options.StateRootPath, "zerotier");
        Directory.CreateDirectory(statePath);

        var identityPath = Path.Combine(statePath, "identity.bin");
        if (!ZeroTierIdentityStore.TryLoad(identityPath, out var identity))
        {
            if (!File.Exists(identityPath) &&
                TryLoadLibztIdentity(options.StateRootPath, out identity))
            {
                ZeroTierIdentityStore.Save(identityPath, identity);
            }
            else
            {
                identity = ZeroTierIdentityGenerator.Generate(cancellationToken);
                ZeroTierIdentityStore.Save(identityPath, identity);
            }
        }
        else if (!identity.LocallyValidate())
        {
            throw new InvalidOperationException($"Invalid identity at '{identityPath}'. Delete it to regenerate.");
        }

        var planet = ZeroTierPlanetLoader.Load(options, cancellationToken);

        return Task.FromResult(new ZeroTierSocket(options, statePath, identity, planet));
    }

    private static bool TryLoadLibztIdentity(string stateRootPath, out ZeroTierIdentity identity)
    {
        identity = default!;
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRootPath);

        var libztSecretPath = Path.Combine(stateRootPath, "libzt", "identity.secret");
        if (!File.Exists(libztSecretPath))
        {
            return false;
        }

        string text;
        try
        {
            text = File.ReadAllText(libztSecretPath);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (!ZeroTierIdentity.TryParse(text, out identity))
        {
            return false;
        }

        if (identity.PrivateKey is null)
        {
            return false;
        }

        return identity.LocallyValidate();
    }

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
            PersistNetworkState(result);
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
        var localAddress = ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ??
                           ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
        if (localAddress is null)
        {
            throw new InvalidOperationException("No managed IP assigned for this network.");
        }

        return await ListenTcpAsync(localAddress, port, cancellationToken).ConfigureAwait(false);
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
        var localAddress = ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ??
                           ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6);
        if (localAddress is null)
        {
            throw new InvalidOperationException("No managed IP assigned for this network.");
        }

        return await BindUdpAsync(localAddress, port, cancellationToken).ConfigureAwait(false);
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
            var localPort = GenerateEphemeralPort();
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

    private static IPAddress[] LoadPersistedManagedIps(string statePath, ulong networkId)
    {
        var networksDir = Path.Combine(statePath, "networks.d");
        var path = Path.Combine(networksDir, $"{networkId:x16}.ips.txt");
        if (!File.Exists(path))
        {
            return Array.Empty<IPAddress>();
        }

        try
        {
            var lines = File.ReadAllLines(path);
            var ips = new List<IPAddress>(lines.Length);
            foreach (var line in lines)
            {
                if (IPAddress.TryParse(line.Trim(), out var ip))
                {
                    ips.Add(ip);
                }
            }

            return ips.ToArray();
        }
        catch (IOException)
        {
            return Array.Empty<IPAddress>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static byte[]? LoadPersistedNetworkConfigDictionary(string statePath, ulong networkId)
    {
        var networksDir = Path.Combine(statePath, "networks.d");
        var path = Path.Combine(networksDir, $"{networkId:x16}.netconf.dict");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void PersistNetworkState(ZeroTierNetworkConfigResult result)
    {
        var networksDir = Path.Combine(_statePath, "networks.d");
        Directory.CreateDirectory(networksDir);

        var dictPath = Path.Combine(networksDir, $"{_options.NetworkId:x16}.netconf.dict");
        File.WriteAllBytes(dictPath, result.DictionaryBytes);

        var ipsPath = Path.Combine(networksDir, $"{_options.NetworkId:x16}.ips.txt");
        File.WriteAllLines(ipsPath, result.ManagedIps.Select(ip => ip.ToString()));
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Ownership transfers to the returned Stream (disposes UserSpaceTcpClient, link, and UDP transport).")]
    private async ValueTask<Stream> ConnectTcpCoreAsync(IPEndPoint? local, IPEndPoint remote, CancellationToken cancellationToken)
    {
        if (remote.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(remote), "Remote port must be between 1 and 65535.");
        }

        if (remote.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork &&
            remote.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            throw new NotSupportedException($"Unsupported address family: {remote.Address.AddressFamily}.");
        }

        if (IPAddress.IsLoopback(remote.Address))
        {
            throw new NotSupportedException("Loopback addresses are not supported in the ZeroTier managed stack.");
        }

        await JoinAsync(cancellationToken).ConfigureAwait(false);

        var (localManagedIpV4, comBytes) = GetLocalManagedIpv4AndInlineCom();

        if (local is not null && local.Address.AddressFamily != remote.Address.AddressFamily)
        {
            throw new NotSupportedException("Local and remote address families must match.");
        }

        if (local is not null && (local.Port < 0 || local.Port > ushort.MaxValue))
        {
            throw new ArgumentOutOfRangeException(nameof(local), "Local port must be between 0 and 65535.");
        }

        var localAddress = local?.Address ?? (remote.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? localManagedIpV4 ?? throw new InvalidOperationException("No IPv4 managed IP assigned for this network.")
            : ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
              ?? throw new InvalidOperationException("No IPv6 managed IP assigned for this network."));

        if (!ManagedIps.Contains(localAddress))
        {
            throw new InvalidOperationException($"Local address '{localAddress}' is not one of this node's managed IPs.");
        }

        var runtime = await GetOrCreateRuntimeAsync(localManagedIpV4, comBytes, cancellationToken).ConfigureAwait(false);
        var remoteNodeId = await runtime.ResolveNodeIdAsync(remote.Address, cancellationToken).ConfigureAwait(false);

        var fixedPort = local is not null && local.Port != 0;
        var fixedLocalPort = fixedPort ? (ushort)local!.Port : (ushort)0;

        IUserSpaceIpLink? link = null;
        ushort localPort = 0;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            localPort = fixedPort ? fixedLocalPort : GenerateEphemeralPort();
            var localEndpoint = new IPEndPoint(localAddress, localPort);

            try
            {
                link = runtime.RegisterTcpRoute(remoteNodeId, localEndpoint, remote);
                break;
            }
            catch (InvalidOperationException) when (!fixedPort && attempt < 31)
            {
            }
        }

        if (link is null)
        {
            throw new InvalidOperationException("Failed to bind TCP to an ephemeral port (too many collisions).");
        }

        var tcp = new UserSpaceTcpClient(
            link,
            localAddress,
            remote.Address,
            remotePort: (ushort)remote.Port,
            localPort: localPort);

        try
        {
            await tcp.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await tcp.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return tcp.GetStream();
    }

    private (IPAddress? LocalManagedIpV4, byte[] InlineCom) GetLocalManagedIpv4AndInlineCom()
    {
        var localManagedIpV4 = ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        var inlineCom = GetInlineCom();
        return (localManagedIpV4, inlineCom);
    }

    private byte[] GetInlineCom()
    {
        var dict = _networkConfigDictionaryBytes;
        if (dict is null)
        {
            throw new InvalidOperationException("Missing network config dictionary (join not completed?).");
        }

        if (!ZeroTierDictionary.TryGet(dict, "C", out var comBytes) || comBytes.Length == 0)
        {
            throw new InvalidOperationException("Network config does not contain a certificate of membership (key 'C').");
        }

        if (!ZeroTierCertificateOfMembershipCodec.TryGetSerializedLength(comBytes, out var comLen))
        {
            throw new InvalidOperationException("Network config contains an invalid certificate of membership (key 'C').");
        }

        if (comLen != comBytes.Length)
        {
            if (ZeroTierTrace.Enabled)
            {
                ZeroTierTrace.WriteLine($"[zerotier] COM length mismatch: dictionary value has {comBytes.Length} bytes, certificate is {comLen} bytes. Truncating inline COM.");
            }

            comBytes = comBytes.AsSpan(0, comLen).ToArray();
        }

        return comBytes;
    }

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
                ZeroTierHelloOk helloOk;
                byte[] rootKey;
                if (_upstreamRoot is { } cachedRoot && _upstreamRootKey is not null)
                {
                    helloOk = cachedRoot;
                    rootKey = _upstreamRootKey;
                }
                else
                {
                    helloOk = await ZeroTierHelloClient
                        .HelloRootsAsync(udp, _identity, _planet, timeout: TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);

                    var root = _planet.Roots.FirstOrDefault(r => r.Identity.NodeId == helloOk.RootNodeId);
                    if (root is null)
                    {
                        throw new InvalidOperationException($"Root identity not found for {helloOk.RootNodeId}.");
                    }

                    rootKey = new byte[48];
                    ZeroTierC25519.Agree(_identity.PrivateKey!, root.Identity.PublicKey, rootKey);
                }

                var localManagedIpsV6 = ManagedIps
                    .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .ToArray();

                var runtime = new ZeroTierDataplaneRuntime(
                    udp,
                    rootNodeId: helloOk.RootNodeId,
                    rootEndpoint: helloOk.RootEndpoint,
                    rootKey: rootKey,
                    localIdentity: _identity,
                    networkId: _options.NetworkId,
                    localManagedIpV4: localManagedIpV4,
                    localManagedIpsV6: localManagedIpsV6,
                    inlineCom: inlineCom);

                try
                {
                    var groups = new List<ZeroTierMulticastGroup>((localManagedIpV4 is not null ? 1 : 0) + localManagedIpsV6.Length);
                    if (localManagedIpV4 is not null)
                    {
                        groups.Add(ZeroTierMulticastGroup.DeriveForAddressResolution(localManagedIpV4));
                    }

                    for (var i = 0; i < localManagedIpsV6.Length; i++)
                    {
                        groups.Add(ZeroTierMulticastGroup.DeriveForAddressResolution(localManagedIpsV6[i]));
                    }

                    await ZeroTierMulticastLikeClient
                        .SendAsync(
                            udp,
                            helloOk.RootNodeId,
                            helloOk.RootEndpoint,
                            rootKey,
                            _identity.NodeId,
                            _options.NetworkId,
                            groups: groups,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (System.Net.Sockets.SocketException)
                {
                    // Best-effort. Some environments restrict certain outbound paths (IPv6, captive portals, etc.).
                }

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

    private static ushort GenerateEphemeralPort()
    {
        Span<byte> buffer = stackalloc byte[2];
        RandomNumberGenerator.Fill(buffer);
        var port = BinaryPrimitives.ReadUInt16LittleEndian(buffer);
        return (ushort)(49152 + (port % (ushort)(65535 - 49152)));
    }
}
