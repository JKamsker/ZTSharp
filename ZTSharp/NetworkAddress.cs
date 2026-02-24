using System.Net;
using System.Net.Sockets;

namespace ZTSharp;

/// <summary>
/// Overlay address assignment model used for future virtual NIC parity.
/// </summary>
public readonly record struct NetworkAddress(IPAddress Address, int PrefixLength)
{
    public AddressFamily AddressFamily => Address.AddressFamily;
}

