# E2E validation

The repo has two opt-in E2E paths:

## Managed overlay E2E via `ztnet`

This validates the legacy managed overlay stack (not the real ZeroTier protocol):

1. Creates a new ZeroTier network via `ztnet`
2. Starts 2 managed overlay nodes (`ZtNode`, `OsUdp` transport)
3. Adds/authorizes those node ids as network members via `ztnet`
4. Performs a ping/pong using `ZtUdpClient`
5. Performs a ping/pong using `ZtOverlayTcpClient` / `ZtOverlayTcpListener`
6. Deletes the network

Run:

```powershell
$env:LIBZT_RUN_E2E="true"
dotnet test -c Release --filter "FullyQualifiedName~Ztnet_NetworkCreate_SpawnTwoClients_And_Communicate_E2E"
```

You can also run the sample:

```powershell
dotnet run -c Release --project samples/JKamsker.LibZt.Samples.ZtNetE2E/JKamsker.LibZt.Samples.ZtNetE2E.csproj
```

## Real ZeroTier E2E (managed-only)

This validates the managed-only real ZeroTier stack by joining a real network and issuing an HTTP request to a peer by its ZeroTier-managed IP.

Run:

```powershell
$env:LIBZT_RUN_ZEROTIER_E2E="1"
$env:LIBZT_ZEROTIER_NWID="9ad07d01093a69e3"
$env:LIBZT_ZEROTIER_URL="http://10.121.15.99:5380/"
dotnet test -c Release --filter "FullyQualifiedName~ZeroTier_JoinAndHttpGet_E2E"
```
