# Persistence

`Node` persists state through `IStateStore` using logical keys.

---

## Key Layout

| Key | Contents |
|:----|:---------|
| `identity.secret` | 32 bytes secret material |
| `identity.public` | 32 bytes public material |
| `planet` / `roots` | Controller/roots payload (compatibility alias) |
| `networks.d/<NWID>.conf` | Joined network config (JSON) |
| `networks.d/<NWID>.addr` | Overlay address assignment (binary) |
| `peers.d/<NWID>/<PEER>.peer` | OS UDP peer endpoint (binary) |

---

## Planet / Roots Compatibility

Upstream tooling and older examples may refer to `planet` and `roots` interchangeably.
Both built-in stores treat them as aliases:

- Reads and writes to `planet` and `roots` resolve to the same backing entry.
- `ListAsync("")` includes `roots` when `planet` exists (mirrors `libzt`-style expectations).

---

## Migration

If you have existing state folders:

- Keep your current files as-is (`planet` or `roots`). The file store normalizes to `planet` internally.
- Network membership is discovered by scanning `networks.d/*.conf` on startup.
