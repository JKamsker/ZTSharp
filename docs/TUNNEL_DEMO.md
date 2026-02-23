# Tunnel demo (local API + overlay reverse proxy + overlay HttpClient)

This demo proves the end-to-end path:

`HttpClient (overlay TCP)` → `ZtOverlayTcpPortForwarder` → `local OS TCP API server`

Important: this uses the library's **managed overlay transport** over `OsUdp` (direct UDP between managed nodes). It does **not** join the real ZeroTier overlay/protocol stack.

## Quick run (PowerShell)

```powershell
pwsh -File scripts/tunnel_demo.ps1
```

## 1) Start a local demo API (OS TCP)

In terminal A:

```powershell
dotnet run -c Release --project samples/JKamsker.LibZt.Samples.DemoApi/JKamsker.LibZt.Samples.DemoApi.csproj -- --port 5005
```

## 2) Start the overlay reverse proxy (expose)

In terminal B:

```powershell
dotnet run -c Release --project samples/JKamsker.LibZt.Cli/JKamsker.LibZt.Cli.csproj -- `
  expose 5005 `
  --network 0xCAFE2001 `
  --transport osudp `
  --udp-port 49001 `
  --listen 28080 `
  --to 127.0.0.1:5005
```

It prints something like:

- `NodeId: 0x..........\n`
- `Local UDP: [::1]:49001`
- `Expose: http://0x..........\n:28080/ -> 127.0.0.1:5005`

Keep this running.

## 3) Call the API through the overlay (call)

In terminal C (replace `<NODE_ID>` with the node id from step 2):

```powershell
dotnet run -c Release --project samples/JKamsker.LibZt.Cli/JKamsker.LibZt.Cli.csproj -- `
  call `
  --network 0xCAFE2001 `
  --transport osudp `
  --udp-port 49002 `
  --peer <NODE_ID>@127.0.0.1:49001 `
  --url http://<NODE_ID>:28080/hello
```

Expected output:

- `HTTP 200 OK`
- JSON body from the demo API.

Note: the native upstream `libzt` variant is intentionally not supported in this repo (managed-only).
