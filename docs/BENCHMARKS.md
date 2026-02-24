# Benchmarks

Benchmark project: `JKamsker.LibZt.Benchmarks/JKamsker.LibZt.Benchmarks.csproj`

Uses BenchmarkDotNet with `MemoryDiagnoser` to report allocations for hot encode/decode and dispatch paths.

---

## Run All Benchmarks

```powershell
dotnet run -c Release --project JKamsker.LibZt.Benchmarks/JKamsker.LibZt.Benchmarks.csproj
```

## Run a Specific Benchmark

Use BenchmarkDotNet's built-in `--filter` flag:

```powershell
dotnet run -c Release --project JKamsker.LibZt.Benchmarks/JKamsker.LibZt.Benchmarks.csproj `
  -- --filter *NodeFrameCodec*
```
