# SharpLink 0.8.38 performance validation

The Generator changes reject invalid models early; runtime changes affect only cancellation exception classification. On Apple M4 with .NET SDK 10.0.102, five interleaved non-incremental HostApplication builds measured 4.94/2.16/1.97/1.88/1.94 seconds for exact 0.8.37 and 2.50/1.92/1.95/1.83/1.89 seconds for the candidate. Medians were 1.97 and 1.92 seconds (-2.5%).

A real TCP unary harness with one pass-through Client and Server interceptor measured three process medians of 39.672/46.857/41.848 microseconds for the baseline and 36.876/46.177/41.831 microseconds for the candidate. Median-of-process medians changed by -0.04%; both allocate 1,584.03 B/op. Latency ranges overlap and no regression is measurable.

The combined gate also passed non-incremental Release with no warnings/errors, Generator 117/117, Unit 483/483, Integration 241/241, 120-second shared-memory Chaos, and NativeAOT TCP smoke.
