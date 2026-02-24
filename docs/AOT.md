# AOT and Trimming

The library aims to stay friendly to Native AOT and trimming.

---

## Current Measures

- **Source-generated JSON serialization** -- `System.Text.Json` usage in `ZtNode.JoinNetworkAsync`
  uses source-generated metadata (`JKamsker.LibZt/ZtJsonContext.cs`) instead of reflection.
- **Trimming enabled** -- the library is marked `IsTrimmable=true` in
  `JKamsker.LibZt/JKamsker.LibZt.csproj`.

---

## Validation

If you plan to publish with AOT or trimming, validate early with your target `dotnet publish` settings:

```powershell
dotnet publish -c Release `
  samples/JKamsker.LibZt.Samples.ZtNetE2E/JKamsker.LibZt.Samples.ZtNetE2E.csproj `
  -p:PublishTrimmed=true
```

Keep an eye on any new reflection-based APIs introduced over time.
