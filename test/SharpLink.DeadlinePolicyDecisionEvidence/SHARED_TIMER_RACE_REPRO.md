# Shared earliest-timer final-arm race observed by #280 evidence

The #280 decision artifact contains repeated cases where a 10 ms deadline completed only when a stale ~30 s timer fired. This is not used as the scheduler performance verdict; it is a separate current-scheduler correctness finding.

The vulnerable finalization shape in `PendingRequestTable.ScanExpiredDeadlines()` is:

```csharp
Volatile.Write(ref _deadlineScanRunning, 0);
var next = Volatile.Read(ref _approximateEarliestDeadline);
if (next != long.MaxValue)
    ArmDeadlineTimer(next);
```

A concurrent registration can lower `_approximateEarliestDeadline` and arm an earlier deadline after the scanner reads `next` but before the scanner calls `ArmDeadlineTimer(next)`. The scanner then overwrites the earlier timer with its stale later deadline. Because the shared approximate value already contains the earlier timestamp, later registrations with later timestamps do not call `Change`, so the short deadline can remain stranded until the stale timer fires.

Required fix properties:

- never overwrite a concurrently-installed earlier timer with a stale later final-arm;
- preserve current single terminal completion path and full request-ID validation;
- no new per-registration lock/shared allocation;
- deterministic regression test should force the final-arm/read-vs-registration interleaving rather than using sleeps;
- benchmark the fix against the Mode A 100%-deadline/rare-expiry control.
