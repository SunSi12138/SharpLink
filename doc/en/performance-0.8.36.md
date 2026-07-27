# SharpLink 0.8.36 performance validation

Chinese: [`../performance-0.8.36.md`](../performance-0.8.36.md)

On Apple M4 / .NET SDK 10.0.102, an independent exact `8f55419` worktree and the candidate ran the same Server `TryAcquireCall`/`ReleaseCall` loop. Each process warmed up 1,000,000 operations, then measured 21 samples of 5,000,000 operations.

Exact-baseline process medians were 5.3769, 5.1399, and 5.1092 ns; candidate medians were 5.5552, 5.1696, and 5.1706 ns. The median of process medians changed from 5.1399 to 5.1706 ns (+0.60%), and both allocate zero. Every paired process difference stayed below the 5% gate.

An initial three-state-read fix measured +6.4% and was rejected. The final ordering publishes the global count before the existing final state check, preserving two state reads and one atomic increment. Other changes are confined to cleanup, Build/Clone, handshake, or a removed failure-only API.

Combined validation passed with a zero-warning/error non-incremental Release build, Generator 108/108, Unit 483/483, and Integration 240/240. The 120-second shared-memory Chaos run recorded 846,971 successes, 331,401 expected failures, zero unexpected failures, 23 restarts, zero Client/Server Errors, successful drain, and five zero active metrics. NativeAOT TCP smoke and the seven-package pre-commit pack passed.
