# SharpLink 0.8.22 deep audit

Chinese: [`../audit-0.8.22.md`](../audit-0.8.22.md)

Against 0.8.21 commit `481989c`, this batch confirmed five P2 improvements: generated DTO Boolean fields accepted non-canonical bit patterns; Rune and decimal fields bypassed their semantic validation; malformed payloads could construct invalid DateOnly, DateTime, and TimeOnly values; and DateTimeOffset fields accepted invalid ticks or offsets while transmitting all six bytes of native-layout padding.

The complete pre-fix Integration run contained 236 tests: all 231 existing tests passed and exactly five new probes failed. The complete pre-fix Generator run contained 84 tests: all 83 existing tests passed and the new emitted-source probe failed. The final implementation retains existing field IDs, fixed wire types, and payload sizes: Boolean uses canonical 0/1 helpers, semantic values use inlinable validated fixed readers, and the DateTimeOffset writer clears padding. Emitted-source counts cover nullable siblings as well.

After the fixes, the non-incremental Release build completed with 0 warnings and 0 errors; Generator 84/84, Unit 445/445, Integration 236/236, the seven-package pack, and fresh-cache package smoke all passed. An initial length-delimited Codec design was rejected after measuring 66/109 ns serialize/deserialize latency. The final fixed-wire design retains allocations and costs only about 1–2 ns overall. See [`performance-0.8.22.md`](performance-0.8.22.md) and [`migration-0.8.22.md`](migration-0.8.22.md).
