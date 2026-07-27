# SharpLink 0.8.34 performance validation

Chinese: [`../performance-0.8.34.md`](../performance-0.8.34.md)

Apple M4 / .NET SDK 10.0.102, with independent Release processes and 0.8.33 commit `35c8cd2` as baseline. Each process used five warmups and 101 measurements.

The shared-memory reader measured 29.564 ns (P25–P75 29.414–29.881) at baseline and 30.046 ns (29.770–30.118) for 0.8.34, with unchanged 40 B/sample: +0.482 ns or 1.63%. An earlier +7.9% design was rejected.

For 40 contracts with two inherited bases and 10 duplicate RPCs each, alternating order produced baseline process medians of 17.084/17.853 ms and final medians of 15.806/17.497 ms. Median-of-medians improved 17.469 -> 16.652 ms (4.68%), while allocation improved 30,720,156 -> 30,654,364 B (0.21%). A repeated per-pair attribute scan that regressed 39.9% and a later order-sensitive >5% design were rejected. The final implementation groups by CLR signature and extracts each method policy once.

Logging classification, the Chaos oracle, and terminal `AdvanceTo` affect failure/teardown paths only. Raw fixtures remain under `artifacts/performance/0.8.34-reader-ab/`, `artifacts/performance/0.8.34-generator-ab/`, and `artifacts/performance/0.8.34-baseline/`.
