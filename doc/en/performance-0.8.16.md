# SharpLink 0.8.16 Performance Validation

Chinese: [`../performance-0.8.16.md`](../performance-0.8.16.md)

Measurements used Apple M4 / .NET 10.0.2, independent processes for 0.8.15 commit `8b6eeaa` and the final candidate, tiered compilation disabled, nine samples per workload, and reversed candidate→baseline plus baseline→candidate order.

Across the final reversed pairs, buffer-pool rent/return measured 8.79–8.85 ns at baseline and 8.18–8.61 ns for the candidate; a 32-byte packet measured 10.40–10.56 ns and 10.19–10.33 ns respectively. Both retained 0 B/op. Unchanged pending register/complete measured 45.80–47.27 ns versus 44.86–46.01 ns at 48 B/op, while flow-credit round trips measured 21.67–22.33 ns versus 21.28–22.00 ns at 0 B/op. No stable hot-path regression was observed.

Runtime Context Build/Dispose measured 636.32–651.93 ns versus 640.66–645.94 ns, both at 4048.13 B/op. Server Build/Stop measured 2.267–2.376 µs versus 2.272–2.324 µs, both at 13224.81 B/op. Deadline slicing, Host lifetime, failure propagation, and capacity validation remain exceptional or configuration-cold paths. The raw driver is retained under `artifacts/performance/0.8.16-lifecycle-bounds-ab/`.
