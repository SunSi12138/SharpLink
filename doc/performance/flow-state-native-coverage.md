# Native population collection after the 364acae2 timeout

At `364acae28ffe5c46e1ad50a361732bc81c461dcc`, Ready Writer run
`36022611815` completed the full JIT population but its NativeAOT collector
stopped at process 6. Native artifact `10818100560` has SHA256
`e3bf957d1d2426a2aaa345706a04802958a6c7d18a03cb8e9791839568908908`:
six complete processes (96 validated rows), one failed TCP/4096-byte process
(exit -6 during A-ready warmup), and one unattempted process. It is not a
complete native performance result. Previous heads' complete native runs do
not fill this head's missing cases.

The native collector now preflights all report/log/exit paths, attempts each
of the eight declared independent processes once, retains timeout and launch
errors, and fails after collection if any process failed. A partial failed
case is not retried. The case deadline, process timeout (180 seconds), job
limit (20 minutes), workload, ordering, affinity, preparation budget, windows
and four rounds per mode are unchanged. External job cancellation can still
leave missing processes; it cannot be treated as completion.

Native coverage runs even after collection failure and records complete,
failed, invalid and missing processes without calculating partial speedups.
The independent full verifier also runs and still requires eight successful
integer exit codes and all 128 validated rows. A JSON boolean false is not
an integer process exit. Diagnostic, wrong-source, incomplete and unexpected
reports remain rejected. Artifacts retain the existing exact-head/attempt
names; no continue-on-error or retry-to-green policy is introduced.

Three deterministic regressions were first executed against the old scripts:
first-failure collection stopped after one rather than eight planned launches;
preflight found an existing final artifact only after seven new launches; and
the verifier accepted boolean false as exit 0. They failed before the fix.
Tests additionally cover OS launch errors, retained log/exit-only failures,
invalid exit types, partial coverage, diagnostic/extra reports and always-run
workflow finalizers. Reprocessing the actual failed native archive still
rejects it as six complete / one failed / one missing; no timing summary is
produced from its partial rows.

This is an evidence-completeness fix, not a TCP stall fix or a Phase B Go
claim. The same-head diagnostic socket control `36022611719` completed both
default-buffer processes as well as the explicit-buffer controls, so its
success alone does not prove a default-buffer correction. Separately, the
failed diagnostic archive `10818625245` shows continuing receiver progress,
empty application send queues, approximately 512 KiB queued in the TCP sender,
and nearly all busy time receive-window-limited. That narrows this failure,
but does not identify every historical timeout or justify changing defaults.
No C# code, production path, credit policy, socket setting or timeout is
changed by this increment. New-head CI must validate it; keep #735 open and
#742 Draft until the remaining production invariants and full performance
acceptance are satisfied.
