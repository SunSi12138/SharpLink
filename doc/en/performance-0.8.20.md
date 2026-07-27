# SharpLink 0.8.20 performance validation

Chinese: [`../performance-0.8.20.md`](../performance-0.8.20.md)

On Apple M4 / .NET SDK 10.0.102, separate processes compared 0.8.19 commit `2d7cd95` with the final candidate while tiered compilation was disabled. After warmup, each valid generated-string workload ran nine samples per process: 2,000,000 operations per contiguous sample and 500,000 per segmented sample, with candidate and baseline runs interleaved.

The final contiguous candidate medians were 33.74–34.98 ns/op versus 34.31–34.36 ns/op for baseline, with overlapping ranges and 64 B/op on both. Segmented candidate medians were 123.71–127.86 ns/op versus 120.99–122.11 ns/op for baseline, with 112 B/op on both; replacement-marker detection costs about 3.5 ns (roughly 3%) and affects only generated string decode, with no new allocation.

Two simpler designs were measured and rejected: using the exception-fallback decoder on every value was about 8% slower, and fully calling `Utf8.IsValid` before decoding was about 10% slower. The final code strictly checks original bytes only when normal decoding produced U+FFFD. This is the smallest measured cost that fully distinguishes valid U+FFFD from malformed UTF-8 without maintaining a custom decoder. The harness is retained at `artifacts/performance/0.8.20-utf8-ab/`.
