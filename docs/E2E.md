# E2E Testing

Two opt-in end-to-end test paths validate each networking stack independently.

---

## Real ZeroTier E2E

Validates the managed-only real ZeroTier stack by joining a live network and issuing an HTTP request
to a peer by its ZeroTier-managed IP.

**Prerequisites:** A configured network ID and a reachable URL inside that network.

```powershell
$env:LIBZT_RUN_ZEROTIER_E2E = "1"
$env:LIBZT_ZEROTIER_NWID    = "9ad07d01093a69e3"
$env:LIBZT_ZEROTIER_URL     = "http://10.121.15.99:5380/"

dotnet test -c Release --filter "FullyQualifiedName~ZeroTier_JoinAndHttpGet_E2E"
```

---

## Managed Overlay E2E (via net)

Validates the legacy managed overlay stack (not the real ZeroTier protocol).

**Prerequisites:** A working `net` auth/session.

**What it does:**

1. Creates a new ZeroTier network via `net`
2. Starts two managed overlay nodes (`Node` with `OsUdp` transport)
3. Authorizes both node IDs as network members via `net`
4. Runs a ping/pong over `ZtUdpClient`
5. Runs a ping/pong over `OverlayTcpClient` / `OverlayTcpListener`
6. Deletes the network

**Run the test:**

```powershell
$env:LIBZT_RUN_E2E = "true"

dotnet test -c Release --filter "FullyQualifiedName~net_NetworkCreate_SpawnTwoClients_And_Communicate_E2E"
```

**Run the sample:**

```powershell
dotnet run -c Release --project samples/ZTSharp.Samples.NetE2E/ZTSharp.Samples.NetE2E.csproj
```
