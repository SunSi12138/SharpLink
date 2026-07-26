# SharpLink 0.8.15 Performance Validation

Chinese: [`../performance-0.8.15.md`](../performance-0.8.15.md)

Apple M4 / .NET 10.0.2 built 0.8.14 commit `b32f846` and the final candidate into separate processes. Tiered compilation was disabled identically, runs were repeated in reversed order, and every workload used nine measurement samples.

The unchanged flow-credit acquire/update path measured 22.05/21.39 ns on the baseline versus 21.00/21.70 ns on the candidate, all at 0 B/op. Pending register/complete measured 45.31/44.41 ns versus 45.07/45.52 ns; direction moved with process order and all runs retained 48 B/op, showing no RPC hot-path regression. Direct Client Build/Stop retained 6576.4 B/op, with repeated baseline/candidate medians around 1.17–1.21/1.19–1.24 µs. Server Build/Stop retained 13224.8 B/op at roughly 2.30–2.36/2.28–2.35 µs. Both are configuration-only paths with unchanged allocation.

Safety snapshots have an explicit configuration-path cost. Known-IP factory construction moved from 25.47–28.75 ns / 256 B to 38.95–40.93 ns / 360 B; creation through the built-in endpoint delegate moved from 47.62–51.38 ns / 240 B to 55.98–65.39 ns / 344 B; and one-time delegate creation moved from 7.05–7.32 ns / 88 B to 11.71–12.17 ns / 144 B. None runs after a connection enters the RPC path. The additional 104 B endpoint snapshot and 56 B delegate snapshot buy immutable configuration and deterministic ownership. The raw driver is under `artifacts/performance/0.8.15-configuration-ownership-ab/`.
