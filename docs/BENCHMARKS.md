# Benchmarks

Benchmark project: `JKamsker.LibZt.Benchmarks/JKamsker.LibZt.Benchmarks.csproj`

Run all benchmarks:

```powershell
dotnet run -c Release --project JKamsker.LibZt.Benchmarks/JKamsker.LibZt.Benchmarks.csproj
```

Filter a single benchmark type (BenchmarkDotNet built-in filtering):

```powershell
dotnet run -c Release --project JKamsker.LibZt.Benchmarks/JKamsker.LibZt.Benchmarks.csproj -- --filter *NodeFrameCodec*
```

The benchmarks use BenchmarkDotNet's `MemoryDiagnoser` to report allocations for hot encode/decode and dispatch paths.

