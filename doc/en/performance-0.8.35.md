# SharpLink 0.8.35 performance validation

Chinese: [`../performance-0.8.35.md`](../performance-0.8.35.md)

On Apple M4 / .NET SDK 10.0.102, an independent exact `044598c` worktree and the candidate each ran three 21-sample Release processes. Every sample builds and disposes 1,000 fixed-transport Clients after warmup.

Allocation improved from 6,536 to 6,168 B/Build (−368 B, −5.63%). Baseline medians were 2,449.7, 2,450.2, and 2,562.6 ns; candidate medians were 2,370.2, 2,267.4, and 2,202.4 ns. The median of process medians improved 7.46%, with every candidate median below the baseline range. Public options snapshot behavior is unchanged. Other fixes affect control/failure paths only.
