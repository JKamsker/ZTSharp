# Persistence

`Node` persists state through `IStateStore` using logical keys.

---

## Key Rules

Built-in stores normalize and validate keys/prefixes:

- Keys are normalized to `/` separators.
- Keys/prefixes must be relative (no rooted paths).
- Keys/prefixes must not contain `:` (drive roots / NTFS ADS on Windows).
- Keys/prefixes must not contain `\0`.
- Keys/prefixes must not contain `.` or `..` segments.

Invalid keys/prefixes throw `ArgumentException`.

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

- Writes always target `planet`.
- Reads/Exists/Delete prefer `planet` if present, otherwise fall back to `roots`.
- When reading `roots` while `planet` is missing, `FileStateStore` may attempt a best-effort one-time migration from `roots` to `planet`.
- `ListAsync("")` includes both keys when either exists.

---

## Migration

If you have existing state folders:

- Keep your current files as-is (`planet` or `roots`). New writes go to `planet`, and reads may migrate `roots` to `planet`.
- Network membership is discovered by scanning `networks.d/*.conf` on startup.

---

## Atomic Writes (FileStateStore)

`FileStateStore.WriteAsync` uses a best-effort atomic replace strategy:

- Write to a temp file in the same directory (`<path>.tmp.<guid>`).
- Flush to disk.
- Move/replace into the final path.
