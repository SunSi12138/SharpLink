# SharpLink 0.8.23 performance validation

Chinese: [`../performance-0.8.23.md`](../performance-0.8.23.md)

On Apple M4 / .NET SDK 10.0.102, independent Release processes compared 0.8.22 commit `3a4338d` with the final candidate. Every workload used three warmup rounds, nine measured samples, and interleaved 16-element array serialize/deserialize reruns.

Ordinary `int[]` measured about 10.1/17.0 ns in the final candidate versus 10.2–10.3/17.0 ns at baseline, retaining 0/88 B/op. `bool[]` serialization returned to the 7.5–7.6 ns baseline; inbound canonical validation moved deserialize from about 17.5 ns to 22.6 ns, roughly 5 ns total for 16 elements, retaining 40 B/op. The dedicated DateTimeOffset writer measured about 15.4 ns at zero allocation without a regression; complete tick and offset validation adds about 23 ns to a 16-element decode while retaining 280 B/op.

An initial shared write helper increased `int[]` serialization to 12.6 ns, and a second design still measured about 10.8 ns; both were rejected. The final generic serializer restores the original direct copy. Only DateTimeOffset registers a dedicated writer, while inbound validation uses a type gate the JIT can constant-fold. The harness is retained at `artifacts/performance/0.8.23-blit-collection-ab/`.
