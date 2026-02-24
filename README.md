# ZTSharp

[![CI](https://github.com/JKamsker/ZTSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/JKamsker/ZTSharp/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/ZTSharp.svg)](https://www.nuget.org/packages/ZTSharp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/ZTSharp.svg)](https://www.nuget.org/packages/ZTSharp)
![.NET](https://img.shields.io/badge/.NET-10.0-512bd4)

[![License: AGPL-3.0](https://img.shields.io/badge/License-AGPL--3.0-blue.svg)](https://github.com/JKamsker/ZTSharp/blob/master/LICENSE)
[![GitHub stars](https://img.shields.io/github/stars/JKamsker/ZTSharp)](https://github.com/JKamsker/ZTSharp/stargazers)
[![GitHub issues](https://img.shields.io/github/issues/JKamsker/ZTSharp)](https://github.com/JKamsker/ZTSharp/issues)

> **Warning**
> This project is experimental. It has not been audited for security, optimized for performance,
> or hardened for stability. Do not use it in production environments where any of these properties
> are critical.

A fully managed .NET library for ZeroTier networking -- no native binaries, no OS client required.

---

## What is this?

This library provides two independent networking stacks:

**Real ZeroTier Stack** (`ZTSharp.ZeroTier`)
Join existing controller-based ZeroTier networks using normal NWIDs.
User-space TCP/UDP sockets, `HttpClient` integration, IPv4/IPv6 -- all in pure managed code.

**Legacy Overlay Stack** (`ZTSharp`)
A custom managed overlay transport for experimentation and testing.
Not protocol-compatible with the real ZeroTier network.

---

## Quick Start

Join a real ZeroTier network and make an HTTP request:

```csharp
using ZTSharp.ZeroTier;

await using var zt = await ZeroTierSocket.CreateAsync(new ZeroTierSocketOptions
{
    StateRootPath = "path/to/state",
    NetworkId = 0x9ad07d01093a69e3UL
});

using var http = zt.CreateHttpClient();
var body = await http.GetStringAsync("http://10.121.15.99:5380/");
```

---

## Build and Test

```sh
dotnet build -c Release
dotnet test  -c Release
```

---

## Documentation

| Document | Description |
|:---------|:------------|
| [Usage Guide](docs/USAGE.md) | API reference with code examples for both stacks |
| [ZeroTier Stack](docs/ZEROTIER_STACK.md) | Real ZeroTier stack -- status, capabilities, and limitations |
| [ZeroTier Sockets](docs/ZEROTIER_SOCKETS.md) | Managed socket API surface and differences vs OS sockets |
| [Persistence](docs/PERSISTENCE.md) | State store keys, planet/roots compatibility |
| [E2E Testing](docs/E2E.md) | End-to-end validation instructions |
| [Tunnel Demo](docs/TUNNEL_DEMO.md) | Local tunnel demo (reverse proxy over overlay transport) |
| [Benchmarks](docs/BENCHMARKS.md) | Running BenchmarkDotNet benchmarks |
| [AOT / Trimming](docs/AOT.md) | Native AOT and trimming notes |
| [Compatibility](docs/COMPATIBILITY.md) | Known gaps vs upstream `libzt` |
| [Third-Party Notices](THIRD_PARTY_NOTICES.md) | Dependency and license pointers |

---

## License

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for bundled source licenses and NuGet dependency information.
