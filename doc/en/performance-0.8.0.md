# SharpLink 0.8.0 Performance Validation

Chinese: [`../performance-0.8.0.md`](../performance-0.8.0.md)

Baseline: `v0.7.11` / `0151db10c89c8067859daef06ef04e2905cd0e89`. Candidate: the first 0.8.0 audit batch. Both runs used macOS Tahoe 26.4.1 on Apple M4 arm64, .NET SDK 10.0.102 / Runtime 10.0.2, and BenchmarkDotNet 0.15.8 with one launch, three warmups, and ten measurement iterations.

Across the seven runtime hot paths, candidate median latency was 93.09%–101.64% of baseline and allocations were unchanged at 0/0/0/0/128/128/72 bytes. The sole slower result was +1.64% on an unchanged, multimodal call-context path and is treated as local noise. No performance-regression signal was observed. Raw reports remain under the task-local `artifacts/performance/` directory; no remote cross-platform runner was used.
