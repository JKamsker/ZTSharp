# E2E validation via `ztnet`

The repo includes an external smoke test that:

1. Creates a new ZeroTier network via `ztnet`
2. Starts 2 managed nodes (`ZtNode`, `OsUdp` transport)
3. Adds/authorizes those node ids as network members via `ztnet`
4. Performs a ping/pong using `ZtUdpClient`
5. Deletes the network

Important: This does **not** currently validate joining the ZeroTier overlay or communicating with external ZeroTier endpoints. The managed nodes do not implement the upstream ZeroTier protocol stack yet; application traffic is exchanged directly over the library's `OsUdp` transport.

## Run

```powershell
$env:LIBZT_RUN_E2E="true"
dotnet test -c Release --filter "FullyQualifiedName~Ztnet_NetworkCreate_SpawnTwoClients_And_Communicate_E2E"
```

You can also run the sample:

```powershell
dotnet run -c Release --project samples/JKamsker.LibZt.Samples.ZtNetE2E/JKamsker.LibZt.Samples.ZtNetE2E.csproj
```
