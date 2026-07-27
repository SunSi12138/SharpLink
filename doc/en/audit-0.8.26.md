# SharpLink 0.8.26 deep audit

Chinese: [`../audit-0.8.26.md`](../audit-0.8.26.md)

Against 0.8.25 commit `0773496`, this batch confirmed five P2 improvements: `[Oneway]` accepted result-bearing or streaming returns; user parameters could collide with Proxy request/stream locals; DTO members differing only by case crashed constructor analysis while building a case-insensitive dictionary; generated dictionary readers leaked null keys as a BCL `ArgumentNullException`; and non-public default interface helpers became RPC routes.

The complete pre-fix Generator run contained 100 tests: all 96 existing tests passed and exactly four new probes failed. The initial fifth probe claimed that collection-count minimum-byte validation was wrong. Source tracing proved that every nested element length prefix is a fixed UInt32, so the premise was rejected, its test and tentative change were removed, and it does not count. A replacement probe then proved that a private helper appeared in Proxy, Stub, and Manifest output: that revised 101-test pre-fix run had exactly the replacement failure.

`SHARPLINK056` now restricts Oneway methods to non-generic `Task` or `ValueTask`. Generated locals deterministically avoid the complete parameter set. DTO constructor mapping prefers exact matches and accepts case-insensitive fallback only when unique. Dictionary null keys become `DataLoss` before `TryAdd`. Only public ordinary interface methods become routes; non-public abstract methods reuse `SHARPLINK054`, while implemented private helpers are ignored.

The strengthened Generator suite passes 101/101. The exact final tree also passed a non-incremental Release build with 0 warnings/errors, Unit 449/449, Integration 237/237, seven-package pack, and fresh-cache package smoke. A 101-sample, 40-contract/400-method Generator comparison measured 14.755 to 13.530 ms. Compiler-thread allocation increased by 76,640 bytes (0.27%). An isolated 16-key dictionary-guard comparison measured 171.891 to 170.941 ns. See [`performance-0.8.26.md`](../performance-0.8.26.md) and [`migration-0.8.26.md`](../migration-0.8.26.md).
