# #931 R1: proposed original-contract full-product comparison

**Diagnostic-only campaign, frozen for independent review before any new timed process.**
The release owner resolved this scope before execution on2026-10-28: NONE of
this campaign's outcomes alone may lift #931 NoGo or authorize a product merge.
It decides only whether R1 merits further work; a separate predeclared acceptance
contract and final candidate gates would still be required.
Prepared2026-10-28 after the independent hosted native review. The existing #931
NoGo is unchanged. R1 is an empirical candidate, not a predicted performance win.
No R2/helper changes or other optimizations may be stacked onto it.

## Why one comparison is justified, and what it cannot show

Hosted native run40681832353, tool9c0cf9e682deb38c4c321e93db0f11d1338bc7bd,
completed the reviewed two non-timed4352-RPC processes with exact identities and
all four required actual-TCP Tier1 targets. Independent raw review confirmed R1
removes the intercepted success-path duplicate hasNext test/default assignment,
while the long jump remains. Intercepted native4806→5077B, frame288→288; dynamic
2402→2693B, Runtime8146→8193B, Server1984→1984B. Those sizes are NOT an improvement
claim, and adaptivePGO/inlining varies across arms. The cloud attempt003 remains
Failed (Runtime/Server Tier1 missing); it is not overwritten by the hosted result.

The source change has a concrete observed mechanism but an unresolved size/cost
tradeoff. Only a matching full-product comparison can establish whether the new
candidate changes the original regression. No timing result is available yet.

## Exact source and inherited methodology

Original reviewed tool: `9b11cf3bbd0d7f9ceb126f32f38f3ffec6ff9a41`.
Read its paired_931.py, tcp/run_matrix.py, tcp/probe/Program.cs, tcp/summarize.py,
scalar/run.py and README before implementation. Current tool base51f5 preserves
that full-product path and adds unrelated client-only diagnostics; those are not
selected here. Baseline and candidate are full coherent product builds, not
mixed Client/common-Runtime compositions:

- Baseline: `cc7c458b559295e0fc61ec0062bb220faf880a76`.
- R1 candidate: `94adeae886c4ddfd0c02ec2dcb700cd97e9cad80`.
- Historical failed candidate: ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c,
  original full-product run40634900826; retained as historical evidence, not a third
  arm and not pooled with the new trial.
- The test-only c4cb5771dff994f9cd3db8252582d3c0256f7c09 tree has identical src/
  to94adeae8. Its source stamp must never replace the actual measured94adeae8 stamp.
- New tool/workflow exact commit must be independently reviewed,
  published and checked. This plan is not execution authorization by itself.

One standard ubuntu24.04 runner; isolated SDK10.0.102 and private runtime-only
.NET+ASP.NET10.0.13, exact framework pins/rollForwardDisable. Verify official runtime
hashes. Select the first two allowed logical CPUs once and retain topology, model,
OS/runtime/SDK and host observations. They are logical CPUs, not dedicated cores.
Default tiering/PGO/ReadyToRun policy matches original; inherited overrides are
cleared. No forced thresholds or special warmup policy.

Both clean product roots are Release-built, all nine packages fresh-packed and
verified, and TCP/scalar probes built once against baseline before any sampling.
The generated TCP module uses the baseline generator. Copy identical probe/module
and non-product DLLs to both coherent snapshots; pin runtimeconfigs and record
actual source/DLL identities. Stop build servers; mark no-more-build boundary.
No builds or other probes on this runner during the campaign.

### Fixed TCP schedule: exactly48 fresh measured processes

- Four scenarios: static-intercepted/dynamic-intercepted ×1/100 items.
- One worker, exactly one ready connection, pool1/1; request timeout disabled;
  unchanged heartbeat, one passthrough Client interceptor, no Server interceptor.
- Six rounds per scenario. Each process uses unchanged3s warmup and6s measurement.
- Original scenario order is item-major: static1,dynamic1,static100,dynamic100.
  Rotate left by(round−1)mod4 each round. Odd roundsAB, even roundsBA within every
  scenario. No random new order or selected bad-cell-only result.
- Preserve original stop-on-process-failure behavior and every attempted host/log/
  result; unattempted schedule slots are explicit missing evidence. No retry,
  replacement, extra warmup, additional pairs or selection of a favorable subset.
- This is the same frozen methodology, not a claim to recreate identical physical
  hardware or all adaptive JIT decisions from the historical run.

### Inherited scalar diagnostics: exactly14 processes

Static/dynamic ×0/1/100 items ×no deadline/five-minute declared deadline shapes.
Five paired FullOpts processes per arm (10), plus original default-tiered and
PGO-off diagnostics (4), after TCP. Keep original repeats, order, allocation/layout
and native capture. They do not replace actual-TCP or timed-process tier proof.
The earlier native preflight does not prove every call in48 later processes isTier1.

## Minimal tool delta to review before use

Do not rewrite the experiment platform. Preserve original scheduling, Operation,
Stopwatch/timing window, allocations, CPU accounting and scalar harness. A fixed
R1 entry point should select94adeae8 without silently changing legacy ac17 defaults
or allowing Client-only mode to consume unintended revisions.

The original TCP report enumerates on-disk assembly versions and hashes, but not
all actual loaded locations nor raw per-call ticks. Proposed narrow instrumentation
is **after the measured window only**, using already existing arrays:

1. Record unique actually loaded product/probe/module assemblies, locations and
   source versions; compare with frozen snapshot hashes. Preserve old report
   fields and every original metric.
2. Export existing ordered Stopwatch duration arrays plus frequency/count only
   after timing; no new per-call counter, timestamp, dictionary or allocation.
3. Add an offline integrity manifest before summaries: all48 expected raw/host/tick
   entries, successful exit/status, exact scene/runtime/affinity/source/hash and
   complete sample counts. Any absent/inconsistent row prevents complete acceptance;
   raw files are never modified. The original summarizer retains its descriptive
   formulas and failed/incomplete accounting.

The implementation uses a separate `tcp/r1-full-probe` copy, selected only by
`paired_r1.py`; the legacy full-product probe, driver defaults and scalar harness
remain unchanged. Offline fixtures compare the complete setup/Operation/warmup/
measurement source prefix byte-for-byte with9b11cf3b. This proves source-window
identity, not identity of JIT layout across separately compiled probes. If telemetry
cannot be proved non-intrusive to the existing measured operation, stop and revise
the tool plan rather than changing the original timing contract.

## Correctness and acceptance boundaries

Current evidence:

- Existing exact R1:67 focused Unit/5Integration passed on macOS/.NET10.0.11.
- New independently reviewed test-only c4cb577:8 actual focused native-MTP tests
  passed on Linux x64/.NET10.0.13, no skip/cancel/timeout, build0warnings/errors.
  These explicitly cover successful empty/one-item Current counts (timed/untimed),
  getter failure identity/disposal precedence, early disposal and synchronous move
  crossing deadline. Product source unchanged; not a full-suite claim.
- PR956 ordinary CI uses the diagnostic tool branch's inherited source, not a new
  full R1 product release gate. Do not relabel its success as R1 correctness.

Before product acceptance, run/reuse source-applicable full affected ownership,
exception, CTS/ALC, retention/queued, generated ABI and canonical candidate gates.
A proposed immediate focused Linux group is the original67Unit+8new=75 and the
original5Integration on the test-only same-src tree, then complete candidate CI
where required. Record its real SHA/source equivalence, platform and actual count;
no VSTest --filter, no zero-test green, no replacing missing gates with the8 cases.
The full3.0 matrix, package/API/ABI/AOT/trim/soak gates remain separate.

## Frozen analysis and decision meaning — no invented margin

Use original metrics for every cell: throughput, P50/P99/P99.9, CPU/op and B/op;
retain both arms' medians/ranges, six individual paired ratios, ratio of medians,
paired arithmetic mean ratio, and original bootstrap(seed529,20,000 replicates,
indices499/19499). Preserve raw ticks and host observations alongside summaries.

The original tool explicitly supplied **no new performance pass/fail threshold**;
its bootstrap interval is descriptive, not an equivalence/non-inferiority test.
Do not retrofit a3%/5% margin, equate aCI crossing1 to no regression, or combine
samples with historical campaigns to tighten intervals. The user's stretch target
QPS+10%/P99−20% versus2.0.3 is not a waiver for this unrelated baseline contrast.

Predeclared outcomes for this bounded trial:

- Invalid/incomplete input, failures or missing samples: incomplete evidence; stop,
  preserve failure and diagnose the specific cause, with no performance acceptance.
- Persistent adverse cells/paired directions (especially original dynamic100 tail):
  retain #931NoGo; do not append another optimization or favorable samples.
- Mixed/noisy/interval-crossing result: inconclusive, retain NoGo; not equivalence.
- Broad coherent favorable evidence may justify further R1 research only. It does
  NOT establish no-regression, lift #931 NoGo, authorize a product merge, declare
  3.0 Go, or erase the historical ac17 failure. This limitation applies even if all
  descriptive intervals look favorable.

The release owner explicitly selected this **diagnostic-only** contract before
execution. No outcome of this bounded run alone changes NoGo. Any later product
acceptance requires a separately predeclared acceptance contract and all applicable
candidate gates; no criterion or margin may be invented after inspecting these data.
The tool's experiment and completion records carry this restriction explicitly.
Independent exact tool/plan/workflow review is still required before launch.

## Publication/run boundary

A separate tools-only Draft **DO NOT MERGE** validation PR may use an opened-only
trigger against a compatible frozen tool branch, with source/SDK/runtime pins and
always-upload failure evidence, once exact tool/plan review and root approval land.
The proposed `.github/workflows/ownership-r1-full-product.yml` is opened-only,
base `codex/931-client-ablation-tools` at51f5, head
`codex/931-r1-full-product-validation`;90-minute timeout and always-upload all
evidence. No validation PR has been opened and no timing has been launched. Verify its remote tree/SHA and base
before opening once; ordinary PR CI is distinct from the fixed campaign. No merge,
tag, release, permission change, security bypass or automatic rerun is authorized.

## Local preparation checks (not benchmark evidence)

- The separate probe and generated module compile with SDK10.0.102, each with
  zero warnings/errors, against existing test-tree libraries. This is only a
  compile check; the campaign still freshly builds both exact source arms.
- 29 offline Python fixtures pass, including exact48-slot rotation/AB-BA order,
  stop after the first failed process, source/runtime/identity counterexamples,
  missing/raw-tick/shutdown rejection, and unchanged original measured source.
- actionlint1.7.7 and diff whitespace checks pass. No RPC or timed process was
  launched during tool preparation. Compile logs/binlogs are retained locally.
- A new schedule fixture initially assumed argparse defaults stringify as3.0/6.0;
  defaults are integer3/6, whereas the real driver supplies explicit float-parsed
  arguments. The fixture was corrected to supply the real driver's explicit
  arguments; no scheduler, timing default or workload changed.
