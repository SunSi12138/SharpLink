# SharpLink 0.8.44 deep audit

Chinese: [`../audit-0.8.44.md`](../audit-0.8.44.md)

Version 0.8.44 uses exact 0.8.43 commit `9789fbe` as its baseline and closes one P1 plus two P2 engineering root causes. The Server-session, Server-framework, Client-background, and static endpoint-cluster worker manifestations of `Task.WhenAll` exception selection count as one finding.

Shutdown joins now flatten every tracked task's complete exception tree and suppress only explicit cancellation/transport terminal failures. A caller-owned initial Connect failure is still observed by its original caller rather than reported twice by Stop. Synchronous Server terminal-response paths release call admission, request state, service leases, and writers in `finally` even when the bounded send queue rejects the response. Rejected `StreamComplete` and `StreamError` frames likewise close local send-flow state so they cannot retain concurrent-stream slots.

Deterministic pre-fix evidence is retained in the ignored `artifacts/0.8.44-prefx-*` logs. Every post-fix witness and the initial-Connect compatibility control pass. Long DNS jitter, the shared-memory spill gate, and multi-cluster cancellation callbacks were investigated and rejected rather than counted.

All three short full-stream pairs completed without failures. Because their c2s signal was noisy, five strictly interleaved exact-0.8.43/candidate c2s pairs used two-second warmup and ten-second measurement. Paired medians were -0.05% QPS, -0.19% P50, +0.27% P99, and -0.38% CPU per operation, excluding a measurable hot-path regression. See [`../performance-0.8.44.md`](../performance-0.8.44.md).

The final non-incremental Release build has no warnings or errors; Generator is 121/121, Unit 503/503, and Integration 252/252. The final tree's 120-second shared-memory Chaos gate completed 817,533 successes, 294,550 expected failures, zero unexpected failures, and 11 restarts, with no Client/Server Errors, 216 ms maximum recovery, successful drain, and five zero final gauges. Independent-process SharedMemory NativeAOT, seven-package pack, and fresh-cache PackageSmoke passed. This round found new high-value issues, so the consecutive clean-audit counter remains 0/3; the next round is not driven by a finding or version quota.
