# Third-party notices (pointers)

This file is intentionally a *pointer list* and not a legal opinion. Before redistribution, verify all license obligations.

## Bundled sources

- `external/libzt/` – upstream ZeroTier sources + license: `external/libzt/LICENSE.txt`
  - Note: the bundled license file is Business Source License 1.1 with a Change Date of **2026-01-01** and a Change License of **Apache-2.0**.

## NuGet dependencies (license expressions)

Library:

- `Microsoft.Extensions.Logging.Abstractions` (MIT)
- `System.Memory.Data` (MIT)

Build-time / trimming:

- `Microsoft.NET.ILLink.Tasks` (MIT) – auto-referenced by the SDK for trimming/linking scenarios

Tests:

- `xunit` (Apache-2.0)
- `xunit.runner.visualstudio` (Apache-2.0)
- `Microsoft.NET.Test.Sdk` (MIT)
- `coverlet.collector` (MIT)
