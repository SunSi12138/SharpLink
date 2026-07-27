# SharpLink 0.8.15 Deep Audit

Chinese: [`../audit-0.8.15.md`](../audit-0.8.15.md)

Using 0.8.14 commit `b32f846` as the baseline, this batch verified five P2-or-higher defects: a Unix-domain listener deleted any pre-existing path; a Socket Client factory retained a caller-mutable `IPEndPoint`; built-in endpoint delegates retained mutable socket/TLS/shared-memory options; a direct Client builder transferred the same transport/resolver to multiple Clients; and a Server builder both transferred a listener repeatedly and failed to release it after a late Build failure.

The full pre-fix Unit probe ran 417 cases: 410 existing or unaffected cases passed and seven focused cases failed. A short `/tmp` proof showed the old listener replacing an ordinary file; a real loopback proof showed mutation redirecting a factory to port zero; the remaining assertions observed all three frozen option families, unique Client transport/resolver ownership, and successful/failed Server listener transfer paths. The final implementation refuses to replace an existing Unix entry, copies known endpoints, freezes configuration when an endpoint delegate is created, and removes single-owner resources from a builder after transfer or rollback. Unit is 417/417 after the fixes; the non-incremental Release build (0 warnings/0 errors), Generator 83/83, Integration 228/228, seven-package pack, and fresh-cache package smoke all passed.

See [`migration-0.8.15.md`](migration-0.8.15.md) and [`performance-0.8.15.md`](performance-0.8.15.md).
