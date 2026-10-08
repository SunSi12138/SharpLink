# Quiescent-retirement G: unpublished bounded ablation

Final default-only G2 is pinned and independently audited: tree
`398d484fb5b8ab8adb75a74db7d577d929d2dc77`, incremental SHA-256
`a0467bf6f8594b8e4e9fe06170aac9cb1fbfa726aca9ac572f07260082242057`.
The screen label remains G. Final same-binary allocation/counter evidence is
attached and the audit guard is enabled. The superseded all-route G4ec is
preserved separately and is not used by this harness. No helper publishes, commits, changes remote state, or edits F/H
workflows. No instrumentation is part of the timing population.

## Exact controls

- D: actual dev `0fe26024b114bb6e78411a9b86276086c045d03d`.
- F: unchanged fenced lifecycle source
  `0390a381d0aa692ecc1ddc7625dcfc3c9ce842d7`.
- F increment over archived N: SHA-256
  `15e446bb39dec4a3a9d807915e5ed36bd32a163915cb061e40b621f3f4065571`.
- Complete F patch at original recipe06b737df: SHA-256
  `ab80bb0d7717c13c59020cd3ddb720cd2b1deefded2a9d96c774158cce10a9d4`.
- G: final default-only G2 quiescent-retirement increment over exact F.

`prepare.py` reconstructs the original C/N recipe, asserts archived N, then
independently builds F and checks both its full tree and complete source patch.
G will be created separately from that complete F patch, with F asserted again
before the G increment. Final tree, all changed blobs and both complete patches
are retained. Numeric maintainability budgets cannot change. Both arms retain
the original identical measurement harness. `--parent-only` validates the
already-approved F control and cannot create or execute G.

F's full correctness passed2114 default and2114 experimental tests in run
37774091973. Its72-launch screen remained a performance no-go: modest short-path
F/N changes did not resolve the absolute D/F gaps. Those results remain
preserved. Predicted terminal RMW reductions in G are a mechanism claim only;
they do not establish route frequency or performance improvement.

## Same untraced D/F/G screen

The proposed `.github/workflows/742-quiescent-screen.yml` uses one host, the same
four-CPU affinity and runtime flags, and all six permutations of D/F/G. Each
block retains these four workloads in the same order:

1. SHM Client100x4096, original30 warmups/5 seconds/200000-operation cap
2. SHM Server1x16, the same original arguments
3. SHM c8 size1, operation-all, concurrency8,8192/65536 byte windows,
   warmup1/duration2, recording off
4. SHM c8 size10000, the same original arguments

The c8 unary/c2s/s2c/duplex phase order is unchanged. Exactly72 launches and180
result rows are required. All six G/D and G/F raw pairs, CPU/op, allocation/op,
whole c8 allocation/CPU totals, completion counts, actual durations and drain
remain available. No failed launch or unfavorable cell is retried, removed or
averaged into a readiness verdict. All tracked source and actual binary outputs
are hashed before/after; both complete F/G source proofs are archived. Green
collection is not acceptance, and process CPU is not method-level attribution.

## Complete correctness and allocation

The independent correctness job retains all original default/experimental unit
suites, formatter/reference/maintainability checks, original deterministic
allocation budgets, and171 ready-writer checks. Old C registry/admission/terminal
controls and portable old-N first-receive/retirement controls stay unchanged.
Portable F-negative/G2-positive real-session counter controls cover all eight
scenarios and explicitly assert both entry RMW attempts and request monitors.
Default quiescent cases require F15/G7 with two request monitors; named, busy
and retained-mailbox cases require identical F/G costs. All temporary source
changes are restored. Instrumented checkouts and probe build directories live
under runner temporary storage, outside uploaded artifacts. Artifacts retain
exact source/atomic-normalization diffs, tree/blob/source/binary hashes, raw
counters including attempts/CAS successes, expected/actual count proof, and logs.

Pristine F/G receive all120 original legacy lifecycle allocation rows with the
unchanged <=0.01B/request comparison guard. A separate120-row production-mode
matrix passes the same final resolver-slot-union constructor on both arms.
Actual setup uses the author's real RpcSession probe, F/G/G/F process order,
with transport/options outside the interval and every instance disposed. Cold
object layouts and conditional completion-holder costs remain explicit; no
setup cost is subtracted from production measurements.

If the author provides a real-RPC route-frequency counter, it may be added as a
separate, clearly instrumented diagnostic. Such timing must never be substituted
for these uninstrumented populations or described as acceptance. The included eight-case counter controls are untimed business/mechanism
correctness checks; no route-frequency or timing attribution is claimed.

## Current validation boundary

Exact original F reconstruction and complete-patch identity have been checked
locally. The inherited portable validator controls check all original micro
cells (3 sequential/6 contention repetitions) and strict c8 populations. Exact final G2 reconstruction and counter-patch applicability are verified.
Both120-row G2 allocation matrices match F and N using Runtimeaa4dff85..., and
real-session setup remains1448B with fresh empty-route16096B. Hosted complete
correctness and measured performance remain pending. Existing full
acceptance templates remain inactive and unchanged.
