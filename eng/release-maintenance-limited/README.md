# Limited maintenance TLS unary comparison (MTL1)

**Dormant until exact source/workflow review PASS and a separate root-authorized request child.**
No product edits, optimizations, merges, tags, package publication, evidence branch, automatic retry,
or full performance matrix. This is a new finite descriptive protocol, not the historical
maintenance native/layout experiment, its thresholds, or a release gate.

## Frozen subject and lineage

- Product commit `8ae9ee36ebec82f82e03185432601a830c865e95`, tree
  `1f0c9291c7394d012d3241a9cf31482b0734b697` is built once, unmodified.
- TLS/authentication/dynamic replacement setup comes from reviewed #933 harness
  `89d616e4f21c6cb29fc5db446c6dbcc391c218f8:eng/maintenance-hot-cost/probe/TlsNetwork.cs`.
  Contracts, services, project references and isolated build props derive from that harness.
  Native, IL and unrelated parser/micro probes are deliberately absent.
- Build SDK is exactly **10.0.102**, pinned by the fixture's `global.json` (roll-forward disabled).
  Runtime is a separate host containing only .NET/ASP.NET **10.0.14**; `exec --fx-version 10.0.14
  --roll-forward Disable` and loaded runtime/CoreLib identity are verified. Official archive SHA512
  checks are inherited from the reviewed O203N4 install fixture.
- Input receipts retain SHA256 for all product `src/` files and root build configuration, fixture
  inputs, all output binary files (including auxiliary contracts/services), host/CoreLib/CLR/JIT/
  hostpolicy/hostfxr, actual SDK/runtime reports, CPU/OS and runner image. Binary identity is
  checked before and after every process. Product identity is checked before and after build.

## Exactly bounded protocol

One binary, three runtime states, six predeclared ABC permutations, serial execution:

1. `off`, `on-no-update`, `post-update`
2. `off`, `post-update`, `on-no-update`
3. `on-no-update`, `off`, `post-update`
4. `on-no-update`, `post-update`, `off`
5. `post-update`, `off`, `on-no-update`
6. `post-update`, `on-no-update`, `off`

Each of 18 fresh processes performs **1,250 warmup calls, then five rounds of 5,000** sequentially
awaited `Add(10,20)` logical unary RPCs. Every completed logical RPC contributes one positive integer
stopwatch tick duration, not a sampled subset. Each round retains wall-time elapsed ticks,
frequency, integer start/end timestamps and process-wide allocation counters (plus their verified
deltas), all 5,000 raw latencies, and before/after witnesses.
Total measured calls: 450,000; per state: 150,000. Two additional untimed implementation witnesses
occur in each process. No data-dependent extensions or retries exist.

Setup, certificate creation, connect/auth/enrollment, initial witnesses, replacement, warmup,
round-boundary captures and output serialization are excluded from steady-state timing.
Per-RPC timing spans immediately before invoking the held proxy through completion of its await.
Round wall time includes timestamp capture, result checking and latency-array writes. Arrays are
allocated before each measured allocation interval. Allocation is **client + server + runtime,
process-wide** `GC.GetTotalAllocatedBytes(precise: true)`, not client-only allocation. It may include
background work during the interval and is not a pure method allocation figure.

- `off`: maintenance not configured; both client/server enrollment absent and capability absent.
- `on-no-update`: server/client configured, both actual TLS sessions enrolled, capability negotiated;
  no update requested, no replacement used and generation remains unchanged.
- `post-update`: exactly one successful compatible independent-load-context replacement before
  warmup; old service constructed/disposed and replacement constructed. The **same held proxy**
  observes result 30 before and 31 after replacement. Update operation wall-time ticks and
  process-wide allocation start/end counters and deltas are retained separately, never folded into
  steady-state samples.

All states prepare the same independent replacement assembly with `ResultOffset=1`, but only
`post-update` publishes it. Each capture asserts two original physical client/server connection
objects and session objects, Ready, TLS, original authenticated subject and exactly two server
authenticator calls. Generation is stable throughout measured rounds. Heartbeats remain 1h/2h,
request timeout disabled, exact certificate SHA256 pin plus hostname verification retained.

Completed update and each completed round are additionally appended to `<result>.partial.jsonl`:
JSON serialization, writer flush and file `Flush(flushToDisk: true)` occur **outside** timing.
Round metrics are checkpointed immediately after their end counters and before subsequent capture
checks or shutdown can fail. A later exception, timeout or failed shutdown therefore retains earlier
completed samples. Partial checkpoints are **not success receipts** and must never be treated as a
complete comparison: failure still stops the campaign and cannot produce a successful summary.
Successful offline validation requires exactly one matching update plus five matching round records
per process, including newline-terminated complete JSONL, in addition to all final/host receipts.

The validator enforces nonoverlapping monotonic round timestamps and nondecreasing cumulative GC
allocation counters, with update end time/allocation no later than the first measured round's start.
The `SharpLink.Sdk` facade is retained in compile/build manifests but is not artificially loaded or
required to appear in the actual loaded-assembly list; any actually loaded copy must still match.

## Interpretation

Partial checkpoint I/O, JSON serialization and extra live objects can perturb later rounds even
though they are outside the measured intervals. All three states perform the same checkpoint
operations. This instrumentation is **not zero overhead** and the limited results do not remove
that source of environmental/GC perturbation.

Summary emits QPS = total completed measured calls / measured wall time, nearest-rank P99 over
all raw complete-RPC durations, process-wide bytes/RPC, all process metrics, pooled state metrics,
median process metrics and within-order-block ratios relative to `off`. This is a small hosted
loopback TLS serial-unary sample, not independent client/server hardware or concurrent saturation.
Pooled P99 is not the median of process P99s; both are labeled separately. No pass/fail performance
threshold or historical equivalence is claimed. Examine all orders/rounds and noise before deciding
whether further investigation is warranted. Functional/identity failure invalidates the affected
run instead of silently dropping it.

## Review/release and execution

Only `.github/workflows/release-maintenance-limited.yml` and this fixture are added. The workflow
has a single first-attempt push gate on `validation/release-maintenance-limited-20261030-r1` and exact
message `run: maintenance limited MTL1`. Its reviewed parent keeps `REQUEST.json` dormant.
The request child must change **only** that file, be the sole non-force push commit, and name the
exact reviewed parent SHA/tree, review `PASS`, root release `authorized`, and fixed product identity.
`verify_request.py` rejects other event/ref/repository/attempt/schema/content/ancestry combinations.
After the gate, checkout returns to the reviewed parent for every remaining script/workflow byte.

Authorized request schema (substitute actual reviewed SHA/tree only after independent review):

```json
{
  "state": "authorized",
  "campaign": "maintenance-candidate8ae9-tls-unary-MTL1",
  "reviewedToolCommit": "<40 lowercase hex>",
  "reviewedToolTree": "<40 lowercase hex>",
  "reviewStatus": "PASS",
  "rootRelease": "authorized",
  "devSource": "8ae9ee36ebec82f82e03185432601a830c865e95",
  "devTree": "1f0c9291c7394d012d3241a9cf31482b0734b697"
}
```

The build and every measured process run in a fresh process group. Parent timeout/nonzero exit,
any surviving descendant (even after parent exit 0), failed group inspection, or uncertain cleanup
permanently fails the slot and prohibits further launches. `ps` reads, SIGKILL follow-up waits and
quiescence polling are bounded (5s each); terminal host JSON and order receipts retain timeouts and
cleanup failures. No unbounded `wait()` is used. Pure-Python mocks exercise these paths without CLR.

Every raw sample also retains the actual loaded assembly path/hash/context closure. Offline
validation rejects empty/missing/foreign/mismatched framework, product, probe or dynamic assembly
closures; both independent collectible service assemblies must be witnessed. The full private
runtime installation is hashed before/after each process. `validate.py` independently checks all
18 exact commands/order positions, 36 start/terminal receipts, exit/quiescence, nonoverlap, loaded
host/CoreLib, reviewed fixture/product hashes, auxiliary binaries and raw counts/latencies before
recomputing the descriptive summary. A supplied summary is never trusted. Workflow repeats this
validation as a separate step, retaining its own recomputed summary.

Job maximum 45 minutes; build timeout 600s, each process 90s (18 × 90 = 27min worst-case serial
process allowance), plus bounded setup under the job deadline. No reruns are accepted by the gate.
Artifacts retain raw JSON, order receipts, manifests/reports/logs, review request/workflow and the
**actually loaded build DLL closure plus the executable's `.deps.json` and `.runtimeconfig.json`**
for **3 days**. Original DLL/config bytes are copied after successful processes into `actual-binaries/`,
with exact manifest coverage and SHA256 revalidation. This includes the product, probe, dynamic
contracts/services and actually used dependency DLLs, not unused build outputs. Offline validation
rehashes these retained bytes and requires exact coverage of every process's loaded build paths.

SDK/runtime installation directories, runtime archives, unrelated sources/outputs and credentials
are not uploaded. **Runtime bytes are not archived:** offline runtime validation compares retained
loaded hashes to installation manifests and the retained archive SHA512 verification; it cannot
independently rehash original runtime bytes. This is not complete offline runtime provenance
reconstruction. Failure logs and
partial reports are retained with `always()`. Build/run errors fail closed; no performance summary
is produced for an incomplete process set.

Offline tests require only Python stdlib:

```sh
PYTHONDONTWRITEBYTECODE=1 python3 -m unittest discover -s eng/release-maintenance-limited -p 'test_*.py' -v
```

Preparation environment does not have authorized local .NET execution. Only pure-Python tests,
source inspection and git diff checks were performed locally; C# compilation and actual TLS/
performance execution remain **not run** until independent review and explicit root release.
