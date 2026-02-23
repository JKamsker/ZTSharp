# Persistence / state store notes

`ZtNode` persists state through `IZtStateStore` using logical keys.

## Key layout

- `identity.secret` – 32 bytes secret material
- `identity.public` – 32 bytes public material
- `planet` / `roots` – controller/roots payload placeholder (compat alias)
- `networks.d/<NETWORK_ID>.conf` – joined network marker/config (JSON payload)
- `peers.d/<NETWORK_ID>/<PEER_NODE_ID>.peer` – OS UDP peer endpoint (binary)

## `planet` / `roots` compatibility

Upstream tooling (and older examples) may refer to `planet` and `roots` interchangeably.

Both built-in stores treat them as aliases:

- Reads/writes to `planet` and `roots` resolve to the same backing entry.
- `ListAsync("")` includes `roots` when `planet` exists (mirrors `libzt`-style expectations).

## Migration guidance

If you have existing state folders:

- Keep your current files as-is (`planet` or `roots`); the file store normalizes to `planet` internally.
- Network membership is discovered by scanning `networks.d/*.conf` on startup.
