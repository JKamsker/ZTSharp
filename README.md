# JKamsker.LibZt

Fully managed (.NET 10) experimentation-focused replacement surface for `libzt`:

- `ZtNode` for identity + network membership
- `ZtUdpClient` for UDP-like datagrams over the node-to-node transport
- `InMemory` transport for deterministic/offline tests
- `OsUdp` transport for real UDP packet exchange between managed nodes

This repo currently **does not implement the upstream ZeroTier protocol stack** (planet/roots processing, crypto handshakes, virtual NIC / lwIP parity, etc.). The `ztnet` integration is used as an external validation harness for network lifecycle operations and for E2E smoke tests.

## Quick start

Run the unit tests:

```powershell
dotnet test -c Release
```

Run the `ztnet` E2E test (requires working `ztnet` auth/session):

```powershell
$env:LIBZT_RUN_E2E="true"
dotnet test -c Release --filter "FullyQualifiedName~Ztnet_NetworkCreate_SpawnTwoClients_And_Communicate_E2E"
```

Run the sample that creates a network and performs a ping/pong between two managed nodes:

```powershell
dotnet run -c Release --project samples/JKamsker.LibZt.Samples.ZtNetE2E/JKamsker.LibZt.Samples.ZtNetE2E.csproj
```

## Docs

- `docs/USAGE.md` – public API usage guide
- `docs/PERSISTENCE.md` – state store keys + planet/roots compatibility notes
- `docs/E2E.md` – running the `ztnet` smoke test and sample
- `docs/BENCHMARKS.md` – running BenchmarkDotNet benchmarks
- `docs/AOT.md` – AOT/trimming notes
- `docs/COMPATIBILITY.md` – tracked gaps vs upstream `libzt`
- `THIRD_PARTY_NOTICES.md` – dependency/license pointers
