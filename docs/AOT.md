# AOT / trimming notes

The library aims to stay friendly to Native AOT / trimming where feasible.

Current measures:

- `System.Text.Json` usage in `ZtNode.JoinNetworkAsync` uses source-generated metadata (`JKamsker.LibZt/ZtJsonContext.cs`) instead of reflection-based serialization.

If you plan to publish with AOT/trimming, prefer validating with your target `dotnet publish` settings early and keep an eye on any new reflection-based APIs introduced over time.

