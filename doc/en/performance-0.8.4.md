# SharpLink 0.8.4 Performance Validation

Chinese: [`../performance-0.8.4.md`](../performance-0.8.4.md)

On Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8, the 0.8.3 `fb1585e` baseline and 0.8.4 candidate used one launch, five warmups, and fifteen measurement iterations. Cached explicit lookup measured 6.529 to 6.533 ns, fallback lookup 6.515 to 6.504 ns, and generated lookup 8.670 to 6.499 ns. Attached pre-admission dispatch measured 17.656 to 17.098 ns. Every case remained at 0 B/op.

An earlier correct candidate regressed attached dispatch to 19.755 ns (+11.9%) and was rejected. The final bounded-queue publication design restores the original steady-state hot path. Codec entries retain only a snapshot identity token rather than the registration dictionary, avoiding collectible-generation retention while removing the per-lookup generated registration search. Raw reports are retained under `artifacts/performance/0.8.4-hotpath-ab/`.
