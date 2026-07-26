# SharpLink 0.8.18 Performance Validation

Chinese: [`../performance-0.8.18.md`](../performance-0.8.18.md)

Measurements used Apple M4 / .NET 10.0.2, independent processes for 0.8.17 commit `f7d4b8d` and the final candidate, tiered compilation disabled, nine samples per workload, and four interleaved candidate→baseline and baseline→candidate pairs.

Across the four medians, buffer-pool rent/return measured 8.26–8.72 ns at baseline and 8.20–8.76 ns for the candidate, both at 0 B/op. Pending completion measured 44.72–46.52 ns versus 44.71–46.24 ns at 48 B/op, and flow-credit round trips measured 20.99–21.84 ns versus 21.64–21.90 ns at 0 B/op. Independent sample ranges overlap; no stable hot-path regression was observed.

Empty RpcSession Dispose measured 1.542–1.585 µs versus 1.512–1.589 µs, both at 17,904 B/op. Runtime Context Build/Dispose measured 649.42–668.72 ns versus 633.59–667.03 ns, both at 4048.13 B/op. Server Build/Stop measured 2.239–2.369 µs versus 2.234–2.342 µs, both at 13224.81 B/op. A two-stream terminal drain for one request changes from 1280 B to 1312 B, with four medians at 246.20–254.87 ns versus 247.23–273.13 ns. This one 32 B shutdown snapshot invokes user callbacks outside the request lock and prevents a failing callback from stranding later owners. The raw driver is retained under `artifacts/performance/0.8.18-host-drain-stream-ab/`.
