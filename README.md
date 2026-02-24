# JKamsker.LibZt

This repo contains `JKamsker.LibZt`: a fully managed (.NET 10) library with two networking stacks:

`JKamsker.LibZt` provides:

- **Real ZeroTier stack (managed-only MVP)** (`JKamsker.LibZt.ZeroTier`)
  - Join existing controller-based ZeroTier networks (normal NWIDs) without installing the OS ZeroTier client.
  - Dial outbound IPv4 TCP to peers by ZeroTier-managed IP via `HttpClient`.
- **Managed overlay stack (legacy/experimentation)** (`ZtNode`, `ZtUdpClient`, `ZtOverlayTcpClient`, `InMemory`/`OsUdp`)
  - Managed nodes talk to each other using this library's transports (not protocol-compatible with the real ZeroTier network).

See `docs/ZEROTIER_STACK.md` for the current real ZeroTier MVP scope and limitations.

## Quick start

Run the unit tests:

```powershell
dotnet test -c Release
```

Run the `ztnet` E2E test (managed overlay stack; requires working `ztnet` auth/session):

```powershell
$env:LIBZT_RUN_E2E="true"
dotnet test -c Release --filter "FullyQualifiedName~Ztnet_NetworkCreate_SpawnTwoClients_And_Communicate_E2E"
```

Run the real ZeroTier E2E test (managed-only; requires a configured NWID + reachable URL inside that network):

```powershell
$env:LIBZT_RUN_ZEROTIER_E2E="1"
$env:LIBZT_ZEROTIER_NWID="9ad07d01093a69e3"
$env:LIBZT_ZEROTIER_URL="http://10.121.15.99:5380/"
dotnet test -c Release --filter "FullyQualifiedName~ZeroTier_JoinAndHttpGet_E2E"
```

Run the sample that creates a network and performs a ping/pong between two managed nodes:

```powershell
dotnet run -c Release --project samples/JKamsker.LibZt.Samples.ZtNetE2E/JKamsker.LibZt.Samples.ZtNetE2E.csproj
```

Run the tunnel CLI (ngrok-like overlay port forwarder):

```powershell
dotnet run -c Release --project samples/JKamsker.LibZt.Cli/JKamsker.LibZt.Cli.csproj -- --help
```

## Docs

- `docs/USAGE.md` – public API usage guide
- `docs/PERSISTENCE.md` – state store keys + planet/roots compatibility notes
- `docs/E2E.md` – running the `ztnet` smoke test and sample
- `docs/TUNNEL_DEMO.md` – local tunnel demo (reverse proxy + overlay `HttpClient`)
- `docs/BENCHMARKS.md` – running BenchmarkDotNet benchmarks
- `docs/AOT.md` – AOT/trimming notes
- `docs/COMPATIBILITY.md` – tracked gaps vs upstream `libzt`
- `docs/ZEROTIER_STACK.md` – real ZeroTier stack MVP notes
- `THIRD_PARTY_NOTICES.md` – dependency/license pointers
