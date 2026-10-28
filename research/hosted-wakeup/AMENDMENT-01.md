# Pre-execution amendment01: architecture-local fail-stop

**Proposed for independent review before any hosted preparation or collection.**
This wrapper amendment explicitly supersedes only the cross-architecture stop
sentence in `research/native/PLAN.md` §2. The prior rule stopped the whole matrix
on an infrastructure/build/protocol failure. It is **not** claimed unchanged.

The hosted protocol has two independent predeclared architecture schedules:

1. Standard ubuntu-24.04 / native linux-x64: default-c1 D,C; default-c8 D,C;
   timed-c1 D,C; timed-c8 D,C.
2. Standard macos-14 / native osx-arm64: the same fixed eight-cell order.

Within either architecture, the first failed acquisition, preparation, identity
check, collector process, decoder process or infrastructure timeout stops that
architecture's remaining work. It does not cancel the other architecture's
predeclared schedule. This matches workflow `fail-fast:false`; max-parallel1
remains unchanged. A naturally missing Tier1 body remains an evidence gap under
the existing plan, not an automatic process failure or a reason to add samples.

Both successful and failed architectures remain in the report. A completed
architecture cannot replace the failed one, stand in for cross-architecture
coverage or support a combined success claim. There are no retries, replacement
cells, discarded slow/failing samples or adaptive extra warmup. The planned
maximum remains16 distinct probe processes; failure may leave fewer completed
processes, with the unrun suffix explicitly marked unrun.

Preparation itself executes zero probe cells. A failed prepare artifact cannot
be approved for collection. Both architecture receipts must be independently
approved before the single collect request is frozen; repairing failed preparation
requires a separately reviewed request, not an automatic rerun. All original
prepare attempts remain preserved. A collection failure requires an amended
reviewed plan before any further collection; never replay a failed architecture
merely to obtain a green result.

No per-cell workload, source baseline/candidate, runtime/SDK identity, call count,
completion oracle, tier policy, probe watchdog or native raw-file cap is changed.
No QPS/P99 or same-machine claim follows from this orchestration amendment.
