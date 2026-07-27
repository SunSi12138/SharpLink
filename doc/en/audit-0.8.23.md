# SharpLink 0.8.23 deep audit

Chinese: [`../audit-0.8.23.md`](../audit-0.8.23.md)

Against 0.8.22 commit `3a4338d`, this batch confirmed five P2 improvements: Boolean blit collections accepted non-canonical elements; Rune and decimal collections bypassed scalar validation; DateOnly, DateTime, and TimeOnly collections could construct invalid temporal values; DateTimeOffset collections accepted invalid ticks or offsets and propagated six padding bytes per element; and a truncated shared-memory server response leaked raw `EndOfStreamException` from Client Connect.

The complete pre-fix Unit run contained 449 tests: all 445 existing tests passed and exactly four new probes failed. The complete pre-fix Integration run contained 237 tests: all 236 existing tests passed and the deterministic truncated-peer probe failed. The fixes cover array, List, Memory, ReadOnlyMemory, and ImmutableArray Codecs. A dedicated DateTimeOffset writer canonicalizes padding without adding a branch to ordinary blit writes. Handshake I/O is mapped to `Unavailable` only while the server response remains incomplete and cancellation is not responsible.

After the fixes, the non-incremental Release build completed with 0 warnings and 0 errors; Generator 84/84, Unit 449/449, Integration 237/237, the seven-package pack, and fresh-cache package smoke all passed. Two shared-helper designs were rejected after ordinary `int[]` writes moved from about 10.3 ns to 12.6/10.8 ns. The final ordinary path returned to about 10.1 ns. See [`performance-0.8.23.md`](performance-0.8.23.md) and [`migration-0.8.23.md`](migration-0.8.23.md).
