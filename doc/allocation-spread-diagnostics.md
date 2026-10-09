# Unary allocation-spread diagnosis (#387)

This temporary, diagnostic-only workflow investigates the c1/c8 spread failures. It is not a production fix or a replacement allocation certificate. The source under measurement is fixed at `e91f82118bb4d67997bc76b8a579d51460a28c1b`; the workflow records its own validation commit and hashes the protected source/build/budget files. It rejects changes to those paths. SDKs are installed into a job-private directory so a hosted image’s preinstalled 10.0.1xx patches cannot win `latestPatch` selection. The selected build SDK must be exactly 10.0.102 before compilation; a newer SDK allowed by repository roll-forward fails this diagnostic explicitly. The runtime is explicitly pinned to .NET 10.0.12.

## Fixed pass

A single push to the exact `validation/387-allocation-spread-diagnostics` branch starts this validation workflow; it has no PR trigger. Manual dispatch is also available after registration. Do not publish intermediate commits to the trigger branch. After Release builds and validator tests, the workflow executes exactly once, in order:

1. Existing gate policy self-test.
2. Unmodified c1/c8 gate in a fresh process.
3. Unmodified c1/c8 gate with the existing `--inject-bytes-per-operation 512` option in another process. Both cases must complete and exceed their median allocation budget. A setup failure or spread-only failure cannot satisfy this control.
4. EventPipe calibration with known allocation callsites before, inside and after explicit EventSource markers. Trace conversion, event loss, marker preservation and allocation stack attribution are checked.
5. One c1/c8 trace in another fresh process, with raw trace, all five samples and all scheduling/GC diagnostics retained.

Warmup (512), operations (4000/4096), concurrency (1/8), five complete samples, process-wide counters, and checked-in median/spread budgets are unchanged. The full production gate is untouched. No samples are discarded and there is no conditional repeat or automatic rerun. A failed untraced gate remains a failed workflow even when the traced gate passes. A diagnostic capture can be complete while the budget failure remains unresolved.

## Reading the evidence

`summary.json` distinguishes evidence completion from the untraced gate decision. `execution-index.json` retains every command and exit status. Original gate JSON, logs, runtime/CPU information, identity hashes, negative control, calibration, raw nettrace and converted trace files are retained even on failure. The complete archive is split into 16 MiB parts, uploaded as independent artifacts with SHA256 and size metadata. Reassemble in index order and verify the archive hash before extracting. If more than eight upload slots are needed, packaging fails explicitly, every byte remains in the full overflow archive, and the index records that bounded tool-based transfer is incomplete. No evidence is truncated.

EventPipe allocation ticks are sampled intervals, not a census of every object. The interval's byte count is never assigned to the event's single object type or call stack. Per-type results contain counts and sizes of the sampled objects only. Raw and converted event loss invalidates attribution; zero loss does not prove no allocations were missed by sampling.

The unchanged RPC harness has no EventSource window markers. RPC sample correlation therefore uses its existing UTC diagnostics captured outside the allocation counters, checks consistency with monotonic elapsed time, and remains approximate. Calibration uses exact markers but does not remove that RPC boundary uncertainty. Trace changes scheduling and allocations; a trace pass/failure is diagnostic only. Historical runner failures remain valid evidence regardless of this host's results.

Stop after this fixed pass. Attribute a repair only when a specific source path is supported by the evidence and a deterministic reproducer or controlled intervention. If the pass cannot explain the failure, retain it as inconclusive and design the next bounded experiment explicitly; do not widen budgets, add retries or claim the flake fixed.

Historical sources: [#637 watch-only decision](https://github.com/SunSi12138/SharpLink/pull/637), [#772 diagnostics-only change](https://github.com/SunSi12138/SharpLink/pull/772), [c8 Release Gate failure](https://github.com/SunSi12138/SharpLink/actions/runs/37089229492), and [post-diagnostics c1 failure](https://github.com/SunSi12138/SharpLink/actions/runs/37336971014).
