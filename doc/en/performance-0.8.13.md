# SharpLink 0.8.13 Performance Validation

Chinese: [`../performance-0.8.13.md`](../performance-0.8.13.md)

Apple M4 / .NET 10.0.2 built 0.8.12 commit `db20b9e` and the final candidate into separate processes. Tiered compilation was disabled identically on both sides to remove short-run JIT transitions, and two reversed-order runs collected twelve measurement samples per workload.

Available-data Reader `ReadAsync`/`AdvanceTo` measured 71.64/72.96 ns on the baseline versus 72.36/70.93 ns on the candidate. Default-token control pulse/wait measured 19.88/20.33 ns versus 20.03/20.39 ns; both workloads stayed at 0 B/op. Normal no-spill writer initialize/complete measured 65.54/70.31 ns versus 64.04/65.92 ns, while candidate allocation consistently fell from 280 B to 256 B. There is no regression signal.

An initial candidate added about 1.2 ns to default-token waits and added normal-path cost to writer completion. Splitting cancellable waits into a cold path removed the former. Restoring the one-shot completion shape and moving spill-only gate convergence into a no-inline cold helper removed the latter. The driver and raw logs are under `artifacts/performance/0.8.13-shared-memory-ab/`.
