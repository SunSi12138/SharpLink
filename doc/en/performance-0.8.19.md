# SharpLink 0.8.19 Performance Validation

Chinese: [`../performance-0.8.19.md`](../performance-0.8.19.md)

Apple M4 / .NET 10.0.2 measurements used independent processes built from 0.8.18 commit `1b380e6` and the final candidate, with tiered compilation disabled. Each workload warmed up for 2,000 calls and then ran nine samples of 20,000 RPCs, with candidate-to-baseline and baseline-to-candidate ordering.

TCP unary RPC without interceptors measured 39.98/41.24 µs baseline medians and 39.29/38.98 µs candidate medians. Both retained about 320.01 B/op and their sample ranges overlapped. The default path adds no guard, allocation, or stable latency regression.

With one pass-through Client and one pass-through Server interceptor, baseline medians were 40.75/41.17 µs and candidate medians were 40.04/40.93 µs, again with overlapping sample ranges. Allocation changes from 1552.01 B/op to 1584.01 B/op. The fixed 32 B increase is two single-use continuation guards, 16 B per side, and occurs only when interceptors are explicitly enabled; it prevents duplicate or concurrent `next` calls from executing the business terminal. The raw driver is retained under `artifacts/performance/0.8.19-interceptor-ab/`.
