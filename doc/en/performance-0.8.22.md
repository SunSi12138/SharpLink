# SharpLink 0.8.22 performance validation

Chinese: [`../performance-0.8.22.md`](../performance-0.8.22.md)

On Apple M4 / .NET SDK 10.0.102, independent Release processes compared 0.8.21 commit `481989c` with the final candidate. Every workload used three warmup rounds, nine measured samples, and interleaved baseline/candidate reruns.

Boolean serialize/deserialize measured about 12.0–12.2/11.6–11.9 ns for the baseline and 11.5–11.7/10.4–10.5 ns for the candidate, retaining 0/24 B/op. A six-field DTO containing Rune, decimal, DateOnly, DateTime, TimeOnly, and DateTimeOffset serialized in about 38.0–38.5 ns for the baseline and 38.4–39.1 ns for the candidate. Stable deserialize medians were about 34.6–36.2 ns versus 36.3–37.5 ns. Allocations remained 0/80 B/op, and all new validation adds only about 1–2 ns in absolute terms.

An initial design routed semantic fields through length-delimited built-in Codecs. It retained 0/80 B/op but increased serialize/deserialize latency to about 66/109 ns and was rejected. The final design retains fixed wire and lets the JIT inline type-specific validation; DateTimeOffset only adds padding clearing. The harness is retained at `artifacts/performance/0.8.22-semantic-dto-ab/`.
