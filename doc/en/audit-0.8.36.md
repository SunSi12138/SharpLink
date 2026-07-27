# SharpLink 0.8.36 deep audit

Chinese: [`../audit-0.8.36.md`](../audit-0.8.36.md)

Using exact 0.8.35 commit `8f55419`, this batch proved five P2 improvements: Server Stop could observe zero calls before a racing admission published its global count; Stop did not join asynchronous connection-service cleanup for already drained connections; a profile overwrote an explicitly assigned 8 MiB queue; the public per-call compression switch had no successful semantics; and the public handshake response codec accepted compression capability/profile contradictions.

Admission now publishes the global count before its final Running check and rolls back both counters on a drain race. Retired cleanup is supervised when no call remains, while explicitly uncooperative calls keep bounded deferred cleanup. Flow-control snapshots retain whether the queue was assigned. The dead `EnableCompression` member is removed in favor of existing automatic negotiation and thresholds. Handshake response writer and reader boundaries both enforce capability/profile coherence.

Before fixes, all 479 existing Unit tests passed and only four new probes failed out of 483; a bounded 192,000-schedule probe witnessed the real late admission in 0.47 seconds. All 239 existing Integration tests passed and only the new blocked-disposal Stop probe failed out of 240. After fixes, Unit is 483/483 and Integration is 240/240. Assertion and pseudo-mutation review covers state-check ordering, both counter rollbacks, supervised/deferred cleanup, explicit default assignment, the removed API surface, and both codec directions.

The first admission fix added a third state read and was rejected at 5.3769 -> 5.7210 ns (+6.4%). The final publication-order fix measured 5.1399 -> 5.1706 ns (+0.60%) by median of three process medians, with zero allocation for both.

The final 120-second shared-memory Chaos run completed 846,971 successes, 331,401 expected injected failures, and 23 restarts with zero unexpected failures, zero Client/Server Errors, successful drain, and all five active metrics at zero. NativeAOT printed `AOT_SMOKE_PASS transport=tcp`; the non-incremental Release build has zero warnings/errors, Generator is 108/108, Unit 483/483, Integration 240/240, and all seven 0.8.36 packages packed before commit. See [`../performance-0.8.36.md`](../performance-0.8.36.md) and [`../migration-0.8.36.md`](../migration-0.8.36.md). This round found new improvements, so consecutive clean audit rounds remain 0/3.
