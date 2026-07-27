# SharpLink 0.8.31 performance validation

Chinese: [`../performance-0.8.31.md`](../performance-0.8.31.md)

Apple M4 / .NET SDK 10.0.102 / BenchmarkDotNet 0.15.8 compared independent Release processes at 0.8.30 commit `6ecdac9` and the final candidate, using three warmups, ten measurements, and one launch per process.

An initial writer-identity token measured 3.652 ns, a cold-throw optimization 3.563 ns, and an owner-driven single-argument API 3.526–3.535 ns versus the first 3.446 ns baseline; all were rejected because they introduced measurable work. The final design restores the original `BeginFrame`/`EndFrame` bodies verbatim and only internalizes the duplicate raw writer/token.

A contemporaneous 0.8.30 rerun measured 3.473 ns (99.9% CI 3.454–3.492), while final 0.8.31 measured 3.524 ns (3.509–3.540), about +1.5% and inside the 5% nanosecond-scale gate; both allocate 0 B/op. There is no production method-body difference, so the small independent-process offset is treated as machine noise rather than justification for extra hot-path work. Permanent `RuntimeHotPathBenchmarks.WriteRequestFrame` coverage remains.

Custom endpoint snapshotting runs only at factory construction; Unix identity/preservation only at listener bind/dispose; anonymous-pipe completion once per external child offer; API removal/visibility adds no runtime work. RPC, codec, packet, session, and transport-I/O steady-state paths gain no allocation or branch. Raw artifacts remain under `artifacts/performance/0.8.31-baseline/` and `artifacts/performance/0.8.31-frame-writer-candidate/`.
