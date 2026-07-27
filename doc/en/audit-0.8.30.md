# SharpLink 0.8.30 deep audit

Chinese: [`../audit-0.8.30.md`](../audit-0.8.30.md)

Using 0.8.29 commit `88039d5` as the baseline, this batch verified five P2 improvements: a faulted hosted Run completion during explicit Stop still requested application shutdown; Stop-before-Start allowed an unowned server to be published; `ReturnType.Contains("ValueTask")` mis-emitted `Task<ValueTaskPayload>` contracts; public pipe-backed address values still accepted path syntax; and every local server health poll allocated a 96-byte completed Task.

The fixes make hosted Stop terminal and symmetric across successful/faulted Run observation, model only the canonical outer ValueTask prefix, share logical-name validation between public addresses and concrete transports, and cache the three immutable local health results. Complete pre-fix Generator/Unit runs preserved all 101/464 existing passes and failed only the five new probes; strengthened suites are 102/102 and 468/468.

A comprehensive performance-pattern scan was manually triaged instead of mechanically rewriting cold registration/build/cleanup code. The final non-incremental Release build completed with zero warnings/errors; Integration is 237/237, seven packages and fresh-cache smoke pass. See [`../performance-0.8.30.md`](../performance-0.8.30.md) and [`../migration-0.8.30.md`](../migration-0.8.30.md). Consecutive clean audit rounds remain 0/3.
