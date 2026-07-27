# SharpLink 0.8.33 deep audit

Chinese: [`../audit-0.8.33.md`](../audit-0.8.33.md)

Using 0.8.32 commit `2f3d27c` as the baseline, this batch verified five P2 improvements: incompatible inherited RPC returns were silently collapsed; distinct enum names could emit the same Stub size-field identifier; synchronous Builder rollback could deadlock context-capturing asynchronous cleanup; duplicate Client Hosted Start could lose its existing owner; and Multi-Cluster Hosted Start independently had the same coordinator-ownership defect.

`SHARPLINK057` now rejects incompatible inherited signatures before artifacts are emitted. Type-derived Stub fields use deterministic 64-bit identity suffixes. Client and Server Builder failure rollback completes asynchronous disposal away from the caller context while retaining exception aggregation. Both Hosted Service implementations reject duplicates outside startup cleanup and preserve their existing owner and accessor.

The complete pre-fix Generator run preserved all 102 existing passes and failed only two new probes out of 104. Unit preserved all 474 existing passes and failed only three new probes out of 477. An extreme DNS-jitter hypothesis was withdrawn after an executable probe confirmed saturating conversion on the supported .NET runtime.

The final non-incremental Release build has zero warnings/errors; Generator is 104/104, Unit 477/477, Integration 238/238, and seven-package plus fresh-cache smoke pass. See [`../performance-0.8.33.md`](../performance-0.8.33.md) and [`../migration-0.8.33.md`](../migration-0.8.33.md). Consecutive clean audit rounds remain 0/3.
