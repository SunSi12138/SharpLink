# SharpLink 0.8.14 Deep Audit

Chinese: [`../audit-0.8.14.md`](../audit-0.8.14.md)

Using 0.8.13 commit `7e9c858` as the baseline, this batch verified five P2-or-higher defects: Unix named pipes budgeted UTF-16 characters instead of UTF-8 native-path bytes; listeners rejected only an instance limit of zero; a throwing Client-stream producer cancellation callback interrupted pending completion; TCP Clients accepted port zero even though it is reserved for Server ephemeral bind; and global flow-control FIFO let a head lacking only its own stream credit block otherwise eligible streams.

The pre-fix full probe retained all 404 passing 0.8.13 tests and produced six failing cases: Unicode path length, `-2`/`255` instance limits (one defect), producer callback escape, Client port zero, and an early multi-cluster hypothesis. The latter was withdrawn after token-ownership review; its replacement, cross-stream head-of-line blocking, is directly evidenced by the old global-FIFO branch and the inverted progress assertion. The final implementation budgets the complete UTF-8 path, freezes transport contracts during construction, isolates producer callback failure in a no-inline cold path and reports it through the Client logger, and retains global FIFO only when shared connection credit is insufficient. Unit is 411/411 after the fixes, followed by the complete release gate.

See [`migration-0.8.14.md`](migration-0.8.14.md) and [`performance-0.8.14.md`](performance-0.8.14.md).
