using System.Collections.Concurrent;
using System.Net;

namespace JKamsker.LibZt.Http;

/// <summary>
/// Optional helper for mapping overlay IP addresses (or hostnames) to managed node ids.
/// </summary>
public sealed class OverlayAddressBook
{
    private readonly ConcurrentDictionary<IPAddress, ulong> _addressToNodeId = new();

    public void Add(IPAddress address, ulong nodeId)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfZero(nodeId);
        _addressToNodeId[address] = nodeId;
    }

    public bool Remove(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _addressToNodeId.TryRemove(address, out _);
    }

    public bool TryResolve(IPAddress address, out ulong nodeId)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _addressToNodeId.TryGetValue(address, out nodeId);
    }
}

