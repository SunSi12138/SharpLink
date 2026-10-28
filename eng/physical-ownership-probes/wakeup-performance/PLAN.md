# W1: fenced wakeup write-elision — actual paired TCP diagnostic

## Decision and source identity (not product acceptance)

Does the already reviewed fenced redundant shared-write elision improve actual
contended TCP performance? **Historical f724 baseline**, not current dev and not
release2.0.3: `f724c7aaabdeb5e02bec23f32f6b3c709860e97c` vs published candidate
`e5a35b9bf63d162c3077aec389d402e28bd2fb53` (localc17f434, tree
`ea401a81c4b5a52966f4281acc348f838b9fc935`). No product edit. Code size/native
layout is not the score. QPS, P99/P99.9, allocation and CPU are the observations.
Native collection40729971517 showed emitted fence+reread and omitted shared RMW
on the redundant branch, NOT a free fence, hit-rate proof or speedup. W1 does not
add another trace/probe campaign. Default tiering/PGO is the deployment-like
profile; fixed warmup does NOT establish steady-state Tier1. Native c8 coverage
was incomplete, so do not attribute a null or positive result to Tier1 branch
execution without additional evidence.

## Frozen workload, concurrency and topology

One Linux x64 ubuntu-24.04 hosted job; no paid/custom/admin runner or security
setting changes. Same exact SDK10.0.102 and private official SHA512-verified
Core+ASP.NET10.0.14; roll-forward disabled. Fresh independently built full-product
source arms and packs before timing; identical probe/generated module and all
non-SharpLink dependencies. No product binary overlays or relabeling.

Reuse the original R1/LF1 actual TCP streaming probe: static intercepted 100-item
ServerStreaming calls over loopback TCP, one client/server in each process,
shared pool min=max=1, no request timeout, heartbeat1h, pass-through client
interceptor, zero server interceptors. Each worker independently issues a next
call only after the prior stream is fully consumed and checked. c1 and c8 mean
one/eight concurrent invocation loops SHARING that one connection, not eight
connections. Item order/count/sum and one ready connection checked. No per-item
new instrumentation, no native tracing/disassembly. Existing per-operation
Stopwatch measurement and array recording retained. This is closed-loop load;
not equal-arrival-rate tail measurement and not the final +10%QPS/−20%P99 goal.

CPU policy is predeclared and derived from actual allowed CPUs plus read-only
sysfs socket/core/thread-sibling mapping; lscpu full mapping retained. Choose
lexicographically first same-socket pair with DIFFERENT reported cores and no
sibling relationship for `different-reported-cores`; separately choose first
same-socket/same-core mutual SMT sibling pair for `same-reported-core-smt`.
Both masks contain exactly two logical CPUs. Never hardcode0/1 or0/2. These are
GUEST-REPORTED topology cells, not proven host physical isolation or per-thread
producer/consumer placement. Threads may migrate within their mask. Record
post-measurement per-live-thread allowed masks and vanished thread IDs (exit
races), and reject an observed mismatched mask. c8 is higher offered concurrency,
not an assertion that redundant Signal branch/cache line is hit a known amount.

Unavailable topology: explicit `skippedBeforeTiming`, no substitute pair, no
claim for that hypothesis. If only one topology exists run that distinct24-
process cell set; if neither exists no timing. Never pool topology results.

## Exact bounded schedule and preconditions

Four ordered cells: different-core/c1, different-core/c8, same-SMT/c1,
same-SMT/c8. Six rounds rotate this cell list left by (round−1) mod cell count;
odd rounds baseline→candidate, even candidate→baseline within each pair. Full
schedule JSON written before any build/timing. Both topologies yield exactly48
fresh timed processes, six paired comparisons per cell; one topology yields24.
No randomization based on observed output/load; no extra samples or retries.

Before ANY timing: build/pack all sources, compile identical probe/module,
validate source/tree/hash/runtime identities, stop build servers, then run one
untimed fixed-work real TCP preflight per available topology ×c1/c8 ×arm
(max8 processes, maximum56 probe launches including48 timed samples). Each completes128calls/worker using a shared release gate and
retains successful path/wrapper observations; active in-flight invocation
counter must reach workers (1 or8). Preflight instrumentation exists ONLY in
this untimed mode. Any failure aborts before timing; do not repair by rerunning
measured samples. Both arms must reach actual TCP/intercepted path before the
measurement-start marker. No performance result is computed for preflights.

Each timed process: existing cold path validation;3seconds warmup per worker;
existing GC barrier; same fixed sample arrays and shared start gate;6seconds
requested measurement plus drain of already started calls. QPS denominator is
actual elapsed including drain. CPU is process CPU microseconds/completed call,
allocation is process-wide managed allocated bytes/completed call. These include
both client/server and benchmark overhead. c8 worker arrays consume more memory
than c1, identically within each pair. No timing-stage compile/pack or tracing.
Each process watchdog99seconds (warmup+measurement+90); timeout/nonzero/start
failure logged, continue each remaining predeclared sample exactly once. Global
job ceiling60minutes, no run_attempt>1. All failure logs/evidence preserved.

## Metrics, exact ranks, statistics and evidence

Retain per-worker ordered raw integer duration ticks and Stopwatch frequency,
operation counts, actual elapsed, total allocated bytes and CPU microseconds,
P50/P95/P99/P99.9/max/mean, QPS/items/s, GC counts and first-call observations.
Raw ticks saved after measured CPU/allocation window, before metadata processing.
Percentile rank is exact integer `ceil(n*numerator/denominator)-1`, implemented
using checked64-bit arithmetic with int-sized count:1/2,19/20,99/100,999/1000,
1/1. Boundary tests include1000,2000,1,000,000 and int.MaxValue. Do NOT use floating
99.9/100 ranks. Existing historical probes/results are untouched, not rewritten.

Offline validation recomputes exact percentiles, mean/QPS/allocation/CPU ratios,
all worker/tick counts, loaded source-version/MVID/path/hash identities, runtime,
scene and affinity. Inventory includes ALL scheduled samples and flags missing
raw/host/ticks; no favorable filtering. Reuse original LF1 summary separately
per topology: six paired ratios per cell, medians/ranges, mean paired ratio,
20,000 paired resamples seed529, descriptive95% interval (original index499,
19499). Six pairs and shared host noise do not establish equivalence. Report
all QPS/P50/P99/P99.9/allocation/CPU results including unfavorable ones; P95,
mean,max,GC and full raw data remain available. No added/weakened acceptance
threshold. Incomplete/invalid samples remain explicit and any resulting summary
is partial diagnostic only, never a passed campaign.

Read-only /proc/stat and loadavg before/after each process remain available via
existing execute_sample. Shared-host interference cannot be excluded. No
load-based sample deletion, affinity change, replacement or rerun-to-green.
Artifacts include inputs, schedule/topology, runtime archive receipts, binaries,
build logs/binlogs, all process logs/host records/raw ticks/preflight results,
validation and summaries. include-hidden-files=true. Checkout/initial guard
failures before artifact-root creation are retained as GitHub step logs; later
failures uploaded with always(). No old failed run or evidence is removed.

## Review and launch gate

New isolated paths only; existing LF1/native/original matrix unchanged. Dormant
REQUEST is review-required. Independent review must identify exact tool HEAD and
tree before launch. Authorized request must be sole changed file on single-parent
commit, with pinned reviewed parent/tree and product IDs, on
`codex/wakeup-performance-validation`, exact message
`run: fenced-wakeup paired diagnostic W1`, push event and attempt1 only. No
manual dispatch, unbounded matrix, rerun or outcome-driven amendment. Publish
only after exact review; no merge/release/GitHub APPROVE. Stop after this bounded
campaign and raw review; subsequent experiment needs its own scope decision.

### Failure evidence limits
Caught measurement exceptions (including sample limit) save completed partial
ticks tagged complete=false before failing; they cannot enter the valid summary.
A hard watchdog kill, runtime crash or machine failure may prevent raw tick
flush: the host outcome/log and missing-tick inventory remain, never invent or
replace lost ticks. Summary reports failed/incomplete pairs but computes ratios
only for complete validated pairs, so any failure means partial diagnostic, not
an uncensored complete matrix. No sample deletion or acceptance inference.
