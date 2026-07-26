# SharpLink 0.8.17 Performance Validation

Chinese: [`../performance-0.8.17.md`](../performance-0.8.17.md)

Measurements used Apple M4 / .NET 10.0.2, independent processes for 0.8.16 commit `0e4e1a7` and the final candidate, tiered compilation disabled, nine samples per workload, and four interleaved candidate→baseline and baseline→candidate pairs.

Across the four medians, buffer-pool rent/return measured 8.43–8.66 ns at baseline and 8.34–9.06 ns for the candidate, both at 0 B/op. Pending completion measured 45.23–46.42 ns versus 43.87–46.68 ns at 48 B/op; flow-credit round trips measured 22.04–22.48 ns versus 21.13–22.07 ns at 0 B/op; and handshake request round trips measured 115.28–117.05 ns versus 113.56–118.75 ns at 64 B/op. No stable hot-path regression was observed.

Runtime Context Build/Dispose measured 640.48–656.69 ns versus 638.44–674.62 ns, both at 4048.13 B/op. Server Build/Stop measured 2.262–2.376 µs versus 2.259–2.410 µs, both at 13224.81 B/op. Deep chain-policy cloning changes the TLS client options snapshot from 96 B and 12.32–13.29 ns to 184 B and 82.15–83.95 ns. Deep nested-limit cloning changes admission-controller creation from 1152 B and 268.49–272.55 ns to 1224 B and 273.50–280.89 ns. These fixed costs occur only at security-configuration and lifecycle boundaries to isolate mutable policy; runtime hot-path allocations remain unchanged. The raw driver is retained under `artifacts/performance/0.8.17-negotiation-bounds-ab/`.
