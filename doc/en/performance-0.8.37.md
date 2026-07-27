# SharpLink 0.8.37 performance validation

Chinese: [`../performance-0.8.37.md`](../performance-0.8.37.md)

This batch changes only invalid-model analysis and local naming in generated DTO source. Runtime, Client, Server, and valid DTO execution paths are unchanged.

On Apple M4 / .NET SDK 10.0.102, an independent exact `e4bf5f1` worktree and the candidate alternated five non-incremental Release builds of the same HostApplication with one task-local NuGet cache. Baseline wall times were 4.95, 2.28, 2.13, 1.95, and 1.98 seconds (2.13-second median). Candidate times were 2.96, 2.00, 1.89, 1.88, and 1.87 seconds (1.89-second median), a 0.24-second or 11.3% improvement and no build regression.

The combined gate has a zero-warning/error non-incremental Release build, Generator 113/113, Unit 483/483, and Integration 240/240. A 120-second shared-memory Chaos run recorded 866,582 successes, 337,510 expected failures, zero unexpected failures, 23 restarts, zero Client/Server Errors, and a complete zero-metric drain. NativeAOT TCP smoke, seven pre-commit packages, and a fresh-cache functional package smoke passed.
