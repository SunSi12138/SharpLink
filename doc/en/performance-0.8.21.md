# SharpLink 0.8.21 performance validation

Chinese: [`../performance-0.8.21.md`](../performance-0.8.21.md)

On Apple M4 / .NET SDK 10.0.102, independent processes compared 0.8.20 commit `726992c` with the final candidate, with tiered compilation disabled, warmup, nine samples per workload, and interleaved candidate/baseline order.

Metadata construction retained 136 B/op with both candidate and baseline medians around 13 ns. Two-entry metadata payload sizing moved from roughly 15.1–16.2 ns to 17.0–18.9 ns. Generated ASCII/Unicode string writes moved from roughly 10.6–10.9/15.5–16.0 ns to 15.0–15.3/19.6–20.0 ns, all at 0 B/op. The absolute 2–4 ns cost occurs only on calls explicitly carrying metadata or generated strings.

An initial separate surrogate scan was rejected after short ASCII writes reached about 16.3 ns and metadata construction about 26.7 ns. The final design folds validity into the already-required UTF-8 byte-count/encode operations and leaves metadata snapshot construction unchanged. This is a bounded integrity cost that prevents silent corruption of business fields and routing context. The harness is retained at `artifacts/performance/0.8.21-unicode-ab/`.
