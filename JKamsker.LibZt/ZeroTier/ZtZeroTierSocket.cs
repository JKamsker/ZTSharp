using System.Net;
using JKamsker.LibZt.ZeroTier.Http;
using JKamsker.LibZt.ZeroTier.Internal;
using JKamsker.LibZt.ZeroTier.Protocol;

namespace JKamsker.LibZt.ZeroTier;

public sealed class ZtZeroTierSocket : IAsyncDisposable
{
    private readonly ZtZeroTierSocketOptions _options;
    private readonly string _statePath;
    private readonly ZtZeroTierIdentity _identity;
    private readonly ZtZeroTierWorld _planet;
    private readonly SemaphoreSlim _joinLock = new(1, 1);
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
            identity = ZtZeroTierIdentityGenerator.Generate(cancellationToken);
            ZtZeroTierIdentityStore.Save(identityPath, identity);
        }
        else if (!identity.LocallyValidate())
        {
            throw new InvalidOperationException($"Invalid identity at '{identityPath}'. Delete it to regenerate.");
        }

        var planet = ZtZeroTierPlanetLoader.Load(options, cancellationToken);

        return Task.FromResult(new ZtZeroTierSocket(options, statePath, identity, planet));
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

    public ValueTask<Stream> ConnectTcpAsync(IPEndPoint remote, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remote);
        cancellationToken.ThrowIfCancellationRequested();

        ObjectDisposedException.ThrowIf(_disposed, this);
        throw new NotSupportedException("Real ZeroTier network support is not implemented yet (MVP in progress).");
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _joinLock.Dispose();
        return ValueTask.CompletedTask;
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

    private void PersistNetworkState(ZtZeroTierNetworkConfigResult result)
    {
        var networksDir = Path.Combine(_statePath, "networks.d");
        Directory.CreateDirectory(networksDir);

        var dictPath = Path.Combine(networksDir, $"{_options.NetworkId:x16}.netconf.dict");
        File.WriteAllBytes(dictPath, result.DictionaryBytes);

        var ipsPath = Path.Combine(networksDir, $"{_options.NetworkId:x16}.ips.txt");
        File.WriteAllLines(ipsPath, result.ManagedIps.Select(ip => ip.ToString()));
    }
}
