using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using JKamsker.LibZt.ZeroTier.Http;
using JKamsker.LibZt.ZeroTier.Internal;
using JKamsker.LibZt.ZeroTier.Net;
using JKamsker.LibZt.ZeroTier.Protocol;
using JKamsker.LibZt.ZeroTier.Transport;

namespace JKamsker.LibZt.ZeroTier;

public sealed class ZtZeroTierSocket : IAsyncDisposable
{
    private readonly ZtZeroTierSocketOptions _options;
    private readonly string _statePath;
    private readonly ZtZeroTierIdentity _identity;
    private readonly ZtZeroTierWorld _planet;
    private readonly SemaphoreSlim _joinLock = new(1, 1);
    private readonly SemaphoreSlim _runtimeLock = new(1, 1);
    private ZtZeroTierDataplaneRuntime? _runtime;
    private byte[]? _networkConfigDictionaryBytes;
    private ZtZeroTierHelloOk? _upstreamRoot;
    private byte[]? _upstreamRootKey;
    private bool _joined;
    private bool _disposed;

    private ZtZeroTierSocket(ZtZeroTierSocketOptions options, string statePath, ZtZeroTierIdentity identity, ZtZeroTierWorld planet)
    {
        _options = options;
        _statePath = statePath;
        _identity = identity;
        _planet = planet;
        NodeId = identity.NodeId;
        ManagedIps = LoadPersistedManagedIps(statePath, options.NetworkId);
        _networkConfigDictionaryBytes = LoadPersistedNetworkConfigDictionary(statePath, options.NetworkId);
    }

    public ZtNodeId NodeId { get; private set; }

    public IReadOnlyList<IPAddress> ManagedIps { get; private set; } = Array.Empty<IPAddress>();

    public static Task<ZtZeroTierSocket> CreateAsync(
        ZtZeroTierSocketOptions options,
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

        if (options.PlanetSource == ZtZeroTierPlanetSource.FilePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.PlanetFilePath);
            if (!File.Exists(options.PlanetFilePath))
            {
                throw new FileNotFoundException("Planet file not found.", options.PlanetFilePath);
            }
        }

        if (options.PlanetSource != ZtZeroTierPlanetSource.EmbeddedDefault &&
            options.PlanetSource != ZtZeroTierPlanetSource.FilePath)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid PlanetSource value.");
        }

        var statePath = Path.Combine(options.StateRootPath, "zerotier");
        Directory.CreateDirectory(statePath);

        var identityPath = Path.Combine(statePath, "identity.bin");
        if (!ZtZeroTierIdentityStore.TryLoad(identityPath, out var identity))
        {
            if (!File.Exists(identityPath) &&
                TryLoadLibztIdentity(options.StateRootPath, out identity))
            {
                ZtZeroTierIdentityStore.Save(identityPath, identity);
            }
            else
            {
                identity = ZtZeroTierIdentityGenerator.Generate(cancellationToken);
                ZtZeroTierIdentityStore.Save(identityPath, identity);
            }
        }
        else if (!identity.LocallyValidate())
        {
            throw new InvalidOperationException($"Invalid identity at '{identityPath}'. Delete it to regenerate.");
        }

        var planet = ZtZeroTierPlanetLoader.Load(options, cancellationToken);

        return Task.FromResult(new ZtZeroTierSocket(options, statePath, identity, planet));
    }

    private static bool TryLoadLibztIdentity(string stateRootPath, out ZtZeroTierIdentity identity)
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

        if (!ZtZeroTierIdentity.TryParse(text, out identity))
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

            var result = await ZtZeroTierNetworkConfigClient.FetchAsync(
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
        var handler = new ZtZeroTierHttpMessageHandler(this);
        var client = new HttpClient(handler, disposeHandler: true);
        if (baseAddress is not null)
        {
            client.BaseAddress = baseAddress;
        }

        return client;
    }

    public async ValueTask<ZtZeroTierTcpListener> ListenTcpAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 1 and 65535.");
        }

        await JoinAsync(cancellationToken).ConfigureAwait(false);
        var (localAddress, comBytes) = GetLocalIpv4AndInlineCom();
        var runtime = await GetOrCreateRuntimeAsync(localAddress, comBytes, cancellationToken).ConfigureAwait(false);
        return new ZtZeroTierTcpListener(runtime, localAddress, (ushort)port);
    }

    public async ValueTask<ZtZeroTierUdpSocket> BindUdpAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (port is < 0 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be between 0 and 65535.");
        }

        await JoinAsync(cancellationToken).ConfigureAwait(false);
        var (localAddress, comBytes) = GetLocalIpv4AndInlineCom();
        var runtime = await GetOrCreateRuntimeAsync(localAddress, comBytes, cancellationToken).ConfigureAwait(false);

        if (port != 0)
        {
            return new ZtZeroTierUdpSocket(runtime, localAddress, (ushort)port);
        }

        for (var attempt = 0; attempt < 32; attempt++)
        {
            var localPort = GenerateEphemeralPort();
            try
            {
                return new ZtZeroTierUdpSocket(runtime, localAddress, localPort);
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
        return ConnectTcpCoreAsync(remote, cancellationToken);
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

    private void PersistNetworkState(ZtZeroTierNetworkConfigResult result)
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
        Justification = "Ownership transfers to the returned Stream (disposes ZtUserSpaceTcpClient, link, and UDP transport).")]
    private async ValueTask<Stream> ConnectTcpCoreAsync(IPEndPoint remote, CancellationToken cancellationToken)
    {
        if (remote.Port is < 1 or > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(remote), "Remote port must be between 1 and 65535.");
        }

        if (remote.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            throw new NotSupportedException("Only IPv4 is supported in the ZeroTier TCP MVP.");
        }

        await JoinAsync(cancellationToken).ConfigureAwait(false);

        var (localAddress, comBytes) = GetLocalIpv4AndInlineCom();
        var runtime = await GetOrCreateRuntimeAsync(localAddress, comBytes, cancellationToken).ConfigureAwait(false);

        var remoteNodeId = await runtime.ResolveNodeIdAsync(remote.Address, cancellationToken).ConfigureAwait(false);

        var localPort = GenerateEphemeralPort();
        var localEndpoint = new IPEndPoint(localAddress, localPort);

        var link = runtime.RegisterTcpRoute(remoteNodeId, localEndpoint, remote);
        var tcp = new ZtUserSpaceTcpClient(
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

    private (IPAddress LocalAddress, byte[] InlineCom) GetLocalIpv4AndInlineCom()
    {
        var localAddress = ManagedIps.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
        if (localAddress is null)
        {
            throw new InvalidOperationException("No IPv4 managed IP assigned for this network.");
        }

        var dict = _networkConfigDictionaryBytes;
        if (dict is null)
        {
            throw new InvalidOperationException("Missing network config dictionary (join not completed?).");
        }

        if (!ZtZeroTierDictionary.TryGet(dict, "C", out var comBytes) || comBytes.Length == 0)
        {
            throw new InvalidOperationException("Network config does not contain a certificate of membership (key 'C').");
        }

        if (!ZtZeroTierCertificateOfMembershipCodec.TryGetSerializedLength(comBytes, out var comLen))
        {
            throw new InvalidOperationException("Network config contains an invalid certificate of membership (key 'C').");
        }

        if (comLen != comBytes.Length)
        {
            if (ZtZeroTierTrace.Enabled)
            {
                ZtZeroTierTrace.WriteLine($"[zerotier] COM length mismatch: dictionary value has {comBytes.Length} bytes, certificate is {comLen} bytes. Truncating inline COM.");
            }

            comBytes = comBytes.AsSpan(0, comLen).ToArray();
        }

        return (localAddress, comBytes);
    }

    [global::System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "UDP transport ownership transfers to ZtZeroTierDataplaneRuntime, which is disposed by ZtZeroTierSocket.DisposeAsync.")]
    private async Task<ZtZeroTierDataplaneRuntime> GetOrCreateRuntimeAsync(
        IPAddress localAddress,
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

            var udp = new ZtZeroTierUdpTransport(localPort: 0, enableIpv6: true);
            try
            {
                ZtZeroTierHelloOk helloOk;
                byte[] rootKey;
                if (_upstreamRoot is { } cachedRoot && _upstreamRootKey is not null)
                {
                    helloOk = cachedRoot;
                    rootKey = _upstreamRootKey;
                }
                else
                {
                    helloOk = await ZtZeroTierHelloClient
                        .HelloRootsAsync(udp, _identity, _planet, timeout: TimeSpan.FromSeconds(10), cancellationToken)
                        .ConfigureAwait(false);

                    var root = _planet.Roots.FirstOrDefault(r => r.Identity.NodeId == helloOk.RootNodeId);
                    if (root is null)
                    {
                        throw new InvalidOperationException($"Root identity not found for {helloOk.RootNodeId}.");
                    }

                    rootKey = new byte[48];
                    ZtZeroTierC25519.Agree(_identity.PrivateKey!, root.Identity.PublicKey, rootKey);
                }

                var localManagedIpsV6 = ManagedIps
                    .Where(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                    .ToArray();

                var runtime = new ZtZeroTierDataplaneRuntime(
                    udp,
                    rootNodeId: helloOk.RootNodeId,
                    rootEndpoint: helloOk.RootEndpoint,
                    rootKey: rootKey,
                    localIdentity: _identity,
                    networkId: _options.NetworkId,
                    localManagedIpV4: localAddress,
                    localManagedIpsV6: localManagedIpsV6,
                    inlineCom: inlineCom);

                try
                {
                    var groups = new List<ZtZeroTierMulticastGroup>(1 + localManagedIpsV6.Length)
                    {
                        ZtZeroTierMulticastGroup.DeriveForAddressResolution(localAddress)
                    };

                    for (var i = 0; i < localManagedIpsV6.Length; i++)
                    {
                        groups.Add(ZtZeroTierMulticastGroup.DeriveForAddressResolution(localManagedIpsV6[i]));
                    }

                    await ZtZeroTierMulticastLikeClient
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
