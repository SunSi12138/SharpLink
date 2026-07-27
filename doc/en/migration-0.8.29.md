# SharpLink 0.8.29 migration guide

Chinese: [`../migration-0.8.29.md`](../migration-0.8.29.md)

0.8.29 does not change Protocol v2 framing, payloads, or generated code. Defaults require no migration.

- Named-pipe and shared-memory `name` values are logical identifiers and cannot contain NUL, `/`, or `\\`. Replace path-like names with stable logical names; SharpLink owns the platform path mapping.
- Linux abstract Unix-domain endpoints now remain in the abstract namespace and are never treated as owned filesystem paths.
- `IRpcSession.LastActive` remains a readable/writable UTC diagnostic property, but writing it no longer influences framework heartbeat timeout. Send real protocol traffic instead of mutating the diagnostic timestamp to keep a connection alive.
- Stream registration begun after pending-table disposal now throws `ObjectDisposedException`, matching unary registration. A registration that overlaps disposal completes with `ConnectionClosed`.
- Multi-cluster Ready/Degraded state semantics are unchanged; reads are now allocation-free.
