# SharpLink 0.8.32 deep audit

Chinese: [`../audit-0.8.32.md`](../audit-0.8.32.md)

Using 0.8.31 commit `818f23e` as the baseline, this batch verified five P2 improvements: a bound Unix socket could delete a replacement when identity capture failed; mutable compression profile identity was reread after Build; undefined provider rejection codes could fault handshake encoding; a maximum positive default timeout overflowed before send; and immediate concurrency-only admission allocated three arrays totaling 568 B per call.

Cleanup now preserves an existing UDS path when ownership cannot be proven. Runtime Build freezes each validated compression profile/provider binding. Authentication factories reject undefined codes and the Server trust boundary normalizes constructor-created invalid rejections. Far-future deadlines saturate at `DateTimeOffset.MaxValue`. Admission uses exact slots and transfers a single lease directly on its common synchronous-success path.

The complete pre-fix Unit run preserved all 470 existing passes and failed only four new functional probes out of 474, while recording the 568 B admission allocation. Integration preserved all 237 existing passes and failed only the new authentication probe out of 238. A custom-compression overrun hypothesis was withdrawn after the existing exact-capacity writer stopped it before excess memory was exposed. A pooled admission candidate was also rejected at 93.996 ns / 232 B because it regressed latency.

The final non-incremental Release build has zero warnings/errors; Generator is 102/102, Unit 474/474, Integration 238/238, and seven-package plus fresh-cache smoke pass. See [`../performance-0.8.32.md`](../performance-0.8.32.md) and [`../migration-0.8.32.md`](../migration-0.8.32.md). Consecutive clean audit rounds remain 0/3.
