# Published 2.0.3 vs dev57 representative diagnostic — preparation only

**No RPC, cold, timed or CI launch is authorized by preparation.** Freeze and independently
review the exact tool HEAD before the root authorizes the next stage. This folder is a
separate protocol, not the structural full-matrix scheduler and not the earlier same-ABI
48-process tool that copied one generated module into both arms.

## Question and interpretation

The user's target is **QPS +10%, P99 −20%, without functional shrink**, relative to official
2.0.3. This experiment establishes descriptive distance for four representative intercepted
server-stream cells only. Dev `57f06f172d29fa5cfd4ea8e9b86a23f122df24ab` lacks #931/#933;
no outcome can establish final release acceptance or full functional parity. Closed #962
cross-major interop is not a gate: 3.x is explicitly breaking. Never mix endpoint versions.

Protocol `protocol.json` is authoritative. A=published NuGet2.0.3, source
`4f9295ffb37ce68985051c11bfd9afc0e23166f3`, own Generator/API4; B=exact dev57, own
fresh-packed5product nupkgs and Generator/API5. Same business/fixture sources compile
separately. Only allowed conditional adapter differences: arm name, source SHA, generated
manifest API4/API5 expectations. Generated outputs are preserved and expected to differ;
no cross-arm generated DLL or proxy copying is allowed. Four private non-product dependencies
are identical10.0.12 binaries in both arms. SharpPack/compression are not used by this scalar
contract; this is scope, not evidence those unmeasured features were removed or passed.

## Topology and workload (root amendment, before any samples)

Hosted `ubuntu24.04` only. One fresh process contains real loopback TCP server and client,
C1, pool1, timeout disabled, heartbeat3600s, one client interceptor, zero server interceptors.
The smallest allowed online CPU plus the smallest allowed online CPU on a different
**guest-reported (socket,core)** are frozen for both arms/all processes. Retain actual lscpu
mapping, allowed/selected CPU sets, process affinity, load/CPU/cgroup observations. Missing
mapping stops before cold/timing. No claim of dedicated physical-core/SMT isolation.
The restarted local executor cannot report lscpu mapping; do not run timed work there or
silently reuse the older0/1SMT-limited pool.

Cells: static-intercepted and dynamic-intercepted, stream lengths1 and100. Six paired
AB/BA rounds/cell, rotating cell order,3AB+3BA,48timed fresh processes. Every process runs
cold path/correctness checks before3s warmup then6s closed-loop measurement. Additionally,
8dedicated cold-only processes must all pass before any timed launch. Observed failures
are preserved with stdout/exit/timeout/host records. No replacement/retry/adaptive stopping,
no selective scene omission. A cold failure records all48timed slots as not attempted.

Cold checks verify exact ordered items/count, specified error before/after first item,
sanitized service exception, pending cancellation, early break, consumer exception, explicit
dispose, per-call server-finally/client-logical cleanup, recovery, full shutdown/disposal.
Positive actual wrapper graph and static/dynamic channel assertions are independently
supported by both pinned arm sources, plus own generated manifest API checks. These are
representative functional controls, not a replacement for release-wide feature gates.

All raw per-operation integer duration ticks are retained. QPS is exact
`operations*Stopwatch.Frequency/elapsedTicks`. P99 is exact rational nearest rank
`ceil(n*99/100)`; P99.9 uses999/1000, never a floating-point rank. Per cell report all6paired
QPS and P99 ratios and their exact-rational median. QPS gap to target = `(1.1-ratio)*100`
percentage points; P99 reduction gap = `(ratio-0.8)*100` points. All4cells mandatory; any
missing/failed input suppresses target observations. No pooled-operation quantile, best-run
selection, global pass, confidence or causal claim. Complete observed median target ratios
are descriptive, **not acceptance**.

## Build-only preparation and verification

Retained published cache (already downloaded/verified for #962; reuse, no redundant download):
`../sharplink-wire-gates-20261028/artifacts/protocol-v2-cross-version/attempt-58tb_at4/work/published/packages`
with `evidence/published.provenance.json` under the same attempt. `prepare.py` verifies all5
nupkg/DLL SHA256 and SDK Generator SHA256 against that retained provenance plus nuspec
source commits. Do not use the sibling old `current` packages (f724c7a).

After frozen clean tool HEAD, with a separate pristine exact57product checkout and the
existing dependency cache:

```sh
python eng/official-version-comparison/prepare.py \
  --official-cache "$PUBLISHED_PACKAGES" --official-provenance "$PUBLISHED_PROVENANCE" \
  --dev-source "$CLEAN_DEV57" --dependency-cache "$EXISTING_DEPENDENCY_CACHE" \
  --dotnet "$SDK_10_0_102/dotnet" --output "$NEW_PREPARED_DIR"
python -m unittest discover -s eng/official-version-comparison -p 'test_*.py' -v
```

Prepare only packs/builds; it never starts a probe. Source/export/tool hashes, each arm's
own generated source hashes, binlogs, nupkgs, dependency/snapshot hashes are retained.
Tool clean/SHA/content are checked before and after. Runtimeconfigs pin both private
.NETCore/AspNet10.0.14 with roll-forward disabled. Build verification is not a cold pass.

`run.py` is an explicit future launch entry requiring exact reviewed clean SHA, prepared
snapshot integrity, private runtime-only10.0.14, hosted topology, and root's quiet-window
record `{ "owner": "...", "timestampUtc": "...", "noOtherClrBuildTest": true }`.
All local/hosted preparation and tooling must finish before that coordinated measurement
window. The dormant single-request hosted workflow is included; it must pass independent exact-HEAD review and receive root release before a one-file REQUEST activation commit. No CI has been dispatched. Only public official URLs and fixed provenance are committed; no nupkg binaries or old raw archive are committed. Hosted fetch_published.py retrieves the frozen official packages on the fresh runner and verifies nupkg/DLL/Generator/source pins; local work continues to reuse the existing cache. warm_cache.py populates dev public build dependencies before offline prepare.

After a separately authorized launch, `validate.py --input RUN --prepared PREPARED
--runtime-dotnet RUNTIME/dotnet` independently validates every planned slot, cold records,
identity/path/configuration/affinity/ticks before writing descriptive `summary.json`.
Original raw evidence is never rewritten. Do not upload the blocked older LF1+wakeup raw
archive; that payload remains outside this task's permission.

## Published baseline provenance

`published-cache/provenance.json` contains only the frozen official URLs, source and hash provenance. Existing local nupkgs are reused without downloading. A new hosted runner may fetch those exact official URLs using fetch_published.py and must verify every package, DLL, Generator and nuspec source pin before use. No nupkg mirror is committed. No historical RPC/raw evidence, private logs or blocked LF1+wakeup payload is included.
