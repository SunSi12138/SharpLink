# SharpLink 0.8.13 Deep Audit

Chinese: [`../audit-0.8.13.md`](../audit-0.8.13.md)

Using 0.8.12 commit `db20b9e` as the baseline, this batch verified five P2-or-higher shared-memory lifecycle defects: control-channel disposal stopped joining its writer after the initial timeout; cancellation alone could not wake a control wait; a rejected second PipeReader read replaced the active read's cancellation registration; the same rejection path also cleared the active read's peer-notification state and stranded it after data arrived; and PipeWriter completion released its spill buffer and returned before an active spill flush exited.

The initial full pre-fix run failed four final regressions plus a mapping hypothesis that was later withdrawn, while all 399 existing 0.8.12 tests passed. Ownership review found that mapping hypothesis insufficient for a product fix. Its replacement, the notification-state regression, then failed deterministically on the candidate before read-operation ownership was added. The final fix gives every cancellable control wait its own registration while preserving the default-token fast path; removes overwrite-prone shared Reader/Writer cancellation registrations; atomically owns a Reader operation before changing wait state; and explicitly converges control disposal and spill completion with background operations. Unit is 404/404 after the fix, followed by the complete build, test, performance, and package-smoke gates.

See [`migration-0.8.13.md`](migration-0.8.13.md) and [`performance-0.8.13.md`](performance-0.8.13.md).
