# Pending-owner notification removal: validation migration

The producer ownership repair removes the internal registration notification;
its sole production implementation released the existing selection reservation.
Completion notifications, capacity publication, cancellation/deadline handling and
`MarkRegistered` remain. The replacement tests must not confuse an empty callback,
a post-response recycled slot or an unrelated metric with registration evidence.

## Current-source checks

On source tree `8915e61e5534f6e47a5d679bfbea5c0dcb812eaa`, SDK 10.0.102 and
runtime 10.0.2, the revised explicit lifecycle worker passes all nine regression
scenarios. Its actual registration witness is the exact request's private
`_registered` value before response/reuse. The throwing +1 listener itself records
its pre-MarkRegistered state; no second listener is assumed to run after a throw.
The no-listener control still installs no active metric listener. Legacy
`ownerRegistered` JSON names are explicitly labelled by `registrationWitnessKind`.

The revised matrix passes all 24 CI cells on both pristine
`13928a35df4bff3cd99b14bd1df131aa3eb3bcdb` production and the repaired source, with
byte-identical matrix source and report validator. This includes real TCP/TLS
profiles, missing/duplicate/extra-completion controls and the unchanged 64 MiB
recovery gate. This is correctness and harness-compatibility evidence, not a
performance comparison: the new phase ledger retains ID history longer and must
be used identically on both arms. See [the accounting contract](pending-request-matrix.md#completion-accounting-after-owner-notification-removal).

Both builds completed with zero warnings/errors. The baseline's initial default
parallel build exited unsuccessfully without a compiler diagnostic; its retained
single-process diagnostic build succeeded. Project-mode formatting could not
start its MSBuild named-pipe host in the restricted local environment; supported
folder-mode whitespace verification passed. Maintainability checked 1,310 files
against the unchanged 17 historical allowances. Hosted complete validation is a
separate acceptance gate.

## Historical defect compatibility

The same authored lifecycle worker and driver were overlaid on untouched
`acb160faa72a07835b01d049a2fbcf9070b061df` production, together with the existing
logger probe. All nine **characterization** signatures matched in one run:

- No-listener, ordinary metric and ordinary logger controls pass.
- All three old deadline/response/cancellation/disconnect reuse schedules retain
  the old `DeadlineExceeded` contamination and fail their correctness invariant.
- The -1 metric exception leaves count zero but active ownership one; the next
  registration fails `ResourceExhausted`.
- The +1 metric exception leaves the exact request published but unregistered
  (count/active one, `_registered` zero). The unchanged, specifically armed
  15-second disposal watchdog kills that historical hung worker.
- The throwing logger leaves the original operation incomplete/unreturned.

Characterization exit zero means these precise old bugs reproduced, **not** that
historical production is correct. No original watchdog, timeout, workload,
assertion or parallelism setting was relaxed. No production code was changed in
this historical replay.

## Portable commands

From the repaired checkout, build and run the current gates:

```sh
dotnet build test/SharpLink.UnitTests -c Release -m:1 -nr:false -p:UseSharedCompilation=false
python3 eng/validate-pending-lifecycle.py --mode regression --output artifacts/pending-current
bash eng/run-pending-request-matrix.sh ci artifacts/pending-matrix-current
```

To reproduce the old defects, retain the repaired checkout as `$EVIDENCE`, create
a separate worktree at `acb160faa72a07835b01d049a2fbcf9070b061df`, and copy only
these authored validation files into the matching paths:

- `eng/validate-pending-lifecycle.py`
- `test/SharpLink.UnitTests/Validation/PendingLifecycleValidationProbe.cs`
- `test/SharpLink.UnitTests/Validation/AdmissionDiagnosticsValidationProbe.cs`

Create the `Validation` directory if needed, build with the same command, and run
`python3 eng/validate-pending-lifecycle.py --mode characterize --output artifacts/pending-historical`.
Verify `git diff -- src` is empty. For the pristine-dev matrix replay, use a
separate worktree at `13928a35` and copy only `PendingRequestMatrixEvidenceRunner.cs`,
`PendingRequestMatrixInfrastructure.cs` and `eng/run-pending-request-matrix.sh`
from the repaired checkout. No binaries or private archives are required.
