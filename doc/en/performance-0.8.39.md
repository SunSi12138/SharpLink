# SharpLink 0.8.39 performance validation

On Apple M4 with .NET SDK 10.0.102, an exact `d863dc3` detached worktree and the candidate alternated a real TCP unary harness. Each process warmed both plain and one-Client-plus-one-Server-interceptor paths for 2,000 calls, then measured nine samples of 20,000 calls.

Intercepted baseline process medians were 40.338/46.635/41.267 microseconds; candidate medians were 33.620/47.044/40.831 microseconds. Median-of-process medians improved from 41.267 to 40.831 microseconds (-1.06%), while every run remained approximately 1,584.02-1,584.05 B/op. The ranges overlap and no regression is measurable. The zero-interceptor production path is unchanged.

The combined gate passed non-incremental Release with no warnings/errors, Generator 118/118, Unit 484/484, Integration 246/246, 120-second shared-memory Chaos, NativeAOT TCP, seven-package pack, and fresh-cache TCP/shared-memory functional smoke.
