using System.Net;
using ZTSharp.ZeroTier.Internal;

namespace ZTSharp.Tests;

public sealed class ZeroTierSocketAnyScopeSemanticsTests
{
    [Fact]
    public async Task ConnectWithLocalEndpointAsync_LocalAny_IsNormalizedToManagedIp()
    {
        var marker = new InvalidOperationException("marker");

        var remote = new IPEndPoint(IPAddress.Parse("10.0.0.5"), 443);
        var local = new IPEndPoint(IPAddress.Any, 0);
        var managed = new[] { IPAddress.Parse("10.0.0.2") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await ZeroTierSocketTcpConnector.ConnectWithLocalEndpointAsync(
                ensureJoinedAsync: _ => Task.CompletedTask,
                getManagedIps: () => managed,
                getInlineCom: () => new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 },
                getOrCreateRuntimeAsync: (_, _) => Task.FromException<ZeroTierDataplaneRuntime>(marker),
                local: local,
                remote: remote,
                cancellationToken: CancellationToken.None);
        });

        Assert.Same(marker, ex);
    }

    [Fact]
    public async Task ConnectWithLocalEndpointAsync_LocalIpv6Any_IsNormalizedToManagedIp()
    {
        var marker = new InvalidOperationException("marker");

        var remote = new IPEndPoint(IPAddress.Parse("2001:db8::5"), 443);
        var local = new IPEndPoint(IPAddress.IPv6Any, 0);
        var managed = new[] { IPAddress.Parse("2001:db8::2") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await ZeroTierSocketTcpConnector.ConnectWithLocalEndpointAsync(
                ensureJoinedAsync: _ => Task.CompletedTask,
                getManagedIps: () => managed,
                getInlineCom: () => new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 },
                getOrCreateRuntimeAsync: (_, _) => Task.FromException<ZeroTierDataplaneRuntime>(marker),
                local: local,
                remote: remote,
                cancellationToken: CancellationToken.None);
        });

        Assert.Same(marker, ex);
    }

    [Fact]
    public async Task ListenTcpAsync_GlobalIpv6ScopeMismatch_IsAccepted()
    {
        var marker = new InvalidOperationException("marker");

        var managedIp = IPAddress.Parse("2001:db8::2");
        var localWithScope = new IPAddress(managedIp.GetAddressBytes(), 123);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await ZeroTierSocketBindings.ListenTcpAsync(
                ensureJoinedAsync: _ => Task.CompletedTask,
                getManagedIps: () => new[] { managedIp },
                getInlineCom: () => new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 },
                getOrCreateRuntimeAsync: (_, _) => Task.FromException<ZeroTierDataplaneRuntime>(marker),
                localAddress: localWithScope,
                port: 12345,
                cancellationToken: CancellationToken.None);
        });

        Assert.Same(marker, ex);
    }

    [Fact]
    public async Task ConnectWithLocalEndpointAsync_GlobalIpv6ScopeMismatch_IsAccepted()
    {
        var marker = new InvalidOperationException("marker");

        var managedIp = IPAddress.Parse("2001:db8::2");
        var localWithScope = new IPAddress(managedIp.GetAddressBytes(), 123);
        var remote = new IPEndPoint(IPAddress.Parse("2001:db8::5"), 443);
        var local = new IPEndPoint(localWithScope, 0);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            _ = await ZeroTierSocketTcpConnector.ConnectWithLocalEndpointAsync(
                ensureJoinedAsync: _ => Task.CompletedTask,
                getManagedIps: () => new[] { managedIp },
                getInlineCom: () => new byte[] { 1, 0, 0, 0, 0, 0, 0, 0 },
                getOrCreateRuntimeAsync: (_, _) => Task.FromException<ZeroTierDataplaneRuntime>(marker),
                local: local,
                remote: remote,
                cancellationToken: CancellationToken.None);
        });

        Assert.Same(marker, ex);
    }
}
