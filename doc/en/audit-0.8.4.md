# SharpLink 0.8.4 Deep Audit

Chinese: [`../audit-0.8.4.md`](../audit-0.8.4.md)

Starting from 0.8.3 commit `fb1585e`, this batch audited dynamic Codec publication, Runtime Context lifetime, client-stream admission, and multi-cluster assembly coordination. Five independent P2-or-higher defects were each demonstrated by a deterministic failing test before repair.

Codec lookup now revalidates registration identity after potentially blocking generated factories and fallback resolvers, and rejects a result that crosses Context disposal. Pre-admission replay stays in the existing bounded queue until it can atomically publish the generated dispatcher; registration and reentrant configuration callbacks no longer block under the request-registry lock. Multi-cluster replacement reconciles coordinator routes when its child committed the new generation before old-generation cleanup failed, while preserving the cleanup exception for the caller.

The full-source performance checklist also covered string comparisons, sync-over-async, collection/LINQ allocation, static mutable collections, HTTP/JSON/Regex construction, and sealing candidates. Only the pre-admission sync-over-async hit had reproducible P2 engineering value; cold-path or unproven hits were deliberately left unchanged. See [`../performance-0.8.4.md`](../performance-0.8.4.md) and [`../migration-0.8.4.md`](../migration-0.8.4.md).
