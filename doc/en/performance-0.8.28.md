# SharpLink 0.8.28 performance validation

Chinese: [`../performance-0.8.28.md`](../performance-0.8.28.md)

On Apple M4 / .NET SDK 10.0.102, independent Release processes compared 0.8.27 commit `656271b` with the final candidate after 20,000 warmup operations and 15 samples per case.

Valid binary-error writing measured 11.888 to 11.968 ns/op, with 0 B/op for both. Cold configuration work measured 5.536 to 6.674 ns/op and 56 B/op for socket-option cloning, and 3.335 to 4.826 ns/op with 0 B/op for the combined validation of token-bucket, fixed-window, and sliding-window policies. The absolute 1.14 ns and 1.49 ns costs occur only while freezing configuration.

Alternating candidate/baseline process starts measured the center of two process medians at 8.056 to 8.322 ns for writer rent/return (+3.3%, 0 B/op), 38.531 to 38.217 ns for pending Int32 response (-0.8%, 24 B/op), and 14.510 to 13.904 ns for stream dispatch/consume (-4.2%, amortized 1.333 B/op). Allocations were unchanged and every runtime result remained within the 5% gate. A fixed-order first run showed uniform thermal drift and was excluded from the conclusion.

The boundary harness remains under `artifacts/performance/0.8.28-boundary-ab/`; the runtime harness and baseline worktree remain under `artifacts/performance/0.8.27-runtime-ab/` and `artifacts/performance/0.8.27-baseline/`.
