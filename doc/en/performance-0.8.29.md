# SharpLink 0.8.29 performance validation

Chinese: [`../performance-0.8.29.md`](../performance-0.8.29.md)

On Apple M4 / .NET SDK 10.0.102, independent alternating Release processes compared 0.8.28 commit `a66eccc` with the final candidate after 20,000 warmup operations and 15 samples per case. Values below are the centers of two process medians.

Pending Int32 registration/response measured 37.176 to 37.127 ns/op (-0.1%), retaining 24 B/op; the disposal-linearization state read caused no measurable regression. Ready multi-cluster state reads improved from 8.972 ns / 56 B to 3.189 ns / 0 B.

The combined activity-update and timeout-elapsed microbenchmark measured 26.612 to 30.651 ns/op, a fixed 4.039 ns increase with 0 B/op on both sides. The additional `Stopwatch` sample occurs once per complete inbound frame and provides the monotonic correctness guarantee; heartbeat checks are low-frequency background work. Pending requests, codecs, frame writing, and outbound sends do not execute this code, and the complete pending hot path remained flat.

External verification completed 50,000 Dispose/Rent races without a stranded slot, preserved every abstract UDS serialized byte, and allocated zero bytes across one million multi-cluster State reads. The harness and baseline worktree remain under `artifacts/performance/0.8.29-hotpath-ab/` and `artifacts/performance/0.8.29-baseline/`.
