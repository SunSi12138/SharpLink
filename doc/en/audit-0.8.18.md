# SharpLink 0.8.18 Deep Audit

Chinese: [`../audit-0.8.18.md`](../audit-0.8.18.md)

Using 0.8.17 commit `f7d4b8d` as the baseline, this batch verified five P2-or-higher defects: Hosted Client ownership was lost when token-bound Stop was cancelled; a huge dynamic-module graceful timeout exceeded the native delay range and left the module Draining; a huge send-flush latency overflowed its stopwatch conversion into an immediate flush or pump fault; Server call concurrency accepted `int.MaxValue`, allowing the first deadline scan to request a multi-gigabyte array; and one throwing stream dispatcher interrupted sibling-stream and Session transport cleanup.

The complete pre-fix Unit run executed 432 cases: all 427 prior cases passed and exactly five focused probes failed. The probes directly observed a missing Client Dispose, unregister failure before lease release, a faulted send pump, accepted unbounded call configuration, and an incomplete sibling dispatcher plus transport owner. The final implementation disposes the Hosted owner after Stop, uses bounded long-timer slicing, saturates flush monotonic deadlines, bounds call snapshots at public and internal layers, and drains dispatchers outside the lock before surfacing errors. RpcSession terminal paths isolate user cleanup failures.

After the fixes, the non-incremental Release build completed with 0 warnings and 0 errors; Generator 83/83, Unit 432/432, Integration 228/228, the seven-package pack, and fresh-cache package smoke all passed. The dedicated performance scan did not promote generator-only string operations, build/topology LINQ, or compatibility inheritance surfaces into runtime P2 findings.

See [`migration-0.8.18.md`](migration-0.8.18.md) and [`performance-0.8.18.md`](performance-0.8.18.md).
