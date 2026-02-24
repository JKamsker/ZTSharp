# Tunnel Demo

End-to-end demonstration of the overlay reverse proxy:

```
HttpClient (overlay TCP)  -->  ZtOverlayTcpPortForwarder  -->  Local OS TCP server
```

> **Note:** This uses the **legacy managed overlay transport** over `OsUdp` (direct UDP between
> managed nodes). It does **not** join the real ZeroTier network.

---

## One-Liner

```powershell
pwsh -File scripts/tunnel_demo.ps1
```

---

## Step-by-Step

### 1. Start a Local Demo API

Terminal A:

```powershell
dotnet run -c Release `
  --project samples/JKamsker.LibZt.Samples.DemoApi/JKamsker.LibZt.Samples.DemoApi.csproj `
  -- --port 5005
```

### 2. Start the Overlay Reverse Proxy

Terminal B:

```powershell
dotnet run -c Release `
  --project samples/JKamsker.LibZt.Cli/JKamsker.LibZt.Cli.csproj -- `
  expose 5005 `
  --network 0xCAFE2001 `
  --stack overlay `
  --transport osudp `
  --udp-port 49001 `
  --listen 28080 `
  --to 127.0.0.1:5005
```

It will print something like:

```
NodeId: 0x..........
Local UDP: [::1]:49001
Expose: http://0x..........:28080/ -> 127.0.0.1:5005
```

Keep this running.

### 3. Call Through the Overlay

Terminal C (replace `<NODE_ID>` with the node ID from step 2):

```powershell
dotnet run -c Release `
  --project samples/JKamsker.LibZt.Cli/JKamsker.LibZt.Cli.csproj -- `
  call `
  --network 0xCAFE2001 `
  --stack overlay `
  --transport osudp `
  --udp-port 49002 `
  --peer <NODE_ID>@127.0.0.1:49001 `
  --url http://<NODE_ID>:28080/hello
```

**Expected output:** `HTTP 200 OK` with the JSON body from the demo API.
