# Minimal staged hosted wakeup mechanism protocol

**Environment repair proposed for review.** First hosted prepare run40710903027
failed both inherited-environment preflights before any .NET download/build/probe.
Original artifacts remain retained; no native collection or timing has run.
[AMENDMENT-03.md](AMENDMENT-03.md) defines the exact known-value normalization,
original-environment evidence, pinned Python versions and new request identity.
This adds a transportable preparation/collection wrapper around native tools
originally reviewed at `410c7a71e178385b5eaddf4b525af9e54dd78b4f`. The necessary
source-binding amendment02 in native PLAN.md now targets published remote
`e5a35b9bf63d162c3077aec389d402e28bd2fb53`, whose complete tree equals reviewed
local c17. Hosted binaries are rebuilt with the true new source identity; old
local DLLs/receipts are not reused or relabeled. Apart from this explicit binding
change it does not change production source, per-cell workloads, runtime14,
warmup or native timeout/caps.
It **does amend cross-architecture stopping scope before execution**, as explicitly
defined in [AMENDMENT-01.md](AMENDMENT-01.md): two independent fixed eight-cell
schedules, each fail-stop, all outcomes retained. This is not a zero-change claim
about the original whole-matrix stop rule.
The Debian13 local preparation remains separately identified and is not a cell
or a substitute for either required hosted architecture.

## Exactly three steps, not an unending approval loop

1. **Prepare-only push:** after this wrapper's independent review, push the exact
   reviewed commit to `codex/wakeup-native-prepare-v1`. `REQUEST.json` contains
   only `schema:1, stage:prepare, environmentPolicy:hosted-known-env-v1`. A push path filter watches only this file;
   no merge/default-branch change or repository security/permission setting is
   necessary. New workflow_dispatch files need default-branch presence, so this
   deliberately uses constrained validation-branch pushes instead.
2. **One independent receipt review:** download the two successful prepare artifacts
   `wakeup-prepare-<RID>-<run-id>`. Each contains `prepared.tar.gz`, small
   `review-summary.json`, full build/provenance logs and an artifact manifest.
   Review both real hosts, official hashes, actual source-to-binary provenance,
   receipt schema and all receipt file hashes. The reviewer communicates one
   immutable tuple: prepare run ID/commit plus bundle SHA256 and D/C receipt SHA256
   for each architecture. No new prepare build is needed to accept that tuple.
3. **Collect-only push:** create `codex/wakeup-native-collect-v1` from the same
   reviewed preparation commit; change **only** REQUEST.json to the approved
   tuple (example file is deliberately invalid until filled). Independently review
   this small approval delta, then push it exactly once. Collection downloads
   that run's named artifacts, verifies bundle/receipt hashes, restores the same
   binary bytes and invokes the original collector in its fixed cell/D/C order.
   It contains no restore/build/publish command. Complete raw evidence is uploaded.

No automatic workflow_run chain, workflow rerun or adaptive extra sample is
allowed. `GITHUB_RUN_ATTEMPT != 1` fails closed. Any fresh request commit after a
failure requires an amended reviewed plan; a new push must not be used to replay
failed cells and select the green result. A first failing cell stops that
architecture's remaining cells; the other architecture is retained independently
(`fail-fast:false`). Missing natural Tier1 stays a coverage gap, not a retry reason.

## Identity and portability without rebuilding

Standard labels are exactly **ubuntu-24.04** and **macos-14**, max-parallel1.
Actual Linux distro/version or macOS major, uname/process machine, CPU/affinity or
macOS sysctl data, runner image identifiers and Python version are recorded on
**both prepare and collect machines separately**. Each also records actual
`dotnet --info` host runtime14/architecture, checked against its RID. Probe runtime
and process/OS-architecture checks remain unchanged. Fixed absolute paths are a
restore constraint, **not** evidence that two jobs used the same machine or CPU.

Prepared absolute roots are `/tmp/sharplink-wakeup-native-v1` on Linux and
`/private/tmp/sharplink-wakeup-native-v1` on macOS. The latter avoids macOS's /tmp
symlink violating canonical-path receipt checks. Existing roots are refused.

The bundle includes the actual runtime-only installation, both source trees,
probe/decoder binaries and generated inputs, native/wrapper tool sources, build
logs/package identities, receipts and all files those receipts require. Build SDK
and NuGet caches are not transported: they are unnecessary to execute the frozen
binaries. Their official/input identities and successful build provenance are
retained. SDK/core/ASP archives are downloaded again on collect **only as identical
SHA512-checked bytes required by receipt verification**. The SDK is neither
extracted nor executed in collect. The prepared runtime and binaries are restored
from the bundle, never regenerated from an SDK or source checkout.

The original receipt validator rechecks every bound file, runtimeconfig/deps,
source anchors, actual installed runtime against official archive members, and
approved receipt digest before collection. Wrapper/workflow bytes must also equal
those bundled by prepare; only the approval REQUEST differs. This avoids a
"reviewed receipt, then rebuild different binaries" cycle.

`audit_bundle.py BUNDLE EXPECTED_SHA256` provides a **read-only portable census**
of every receipt-bound file and both receipt digests directly from tar bytes.
It does not extract to the original absolute path or execute ARM binaries. Thus
an independent reviewer on another OS can inspect ARM output without writing
`/private/tmp` or emulating ARM. This census is not an automatic source/build proof
or acceptance verdict; the independent reviewer still checks the required
provenance and official native member hashes.

## Bounded failures and preservation

Both stage entrypoints snapshot and validate inherited runtime/profiler/native-loader
environment **before any dotnet launch, including --info**. Forbidden variables
are rejected even when their value is0; no silent clearing or automatic retry.
Only the validated snapshot is passed onward with documented workspace-path
settings.

- Three official archive acquisitions, no retries: each has a120s budget and512MiB
  cap (a blocked read may add up to15s). SHA512 must match the unchanged official lock.
- Preparation commands:300s each. Original probe watchdog180s is unchanged; wrapper
  collection-command cap210s and decoder cap30s are infrastructure limits, not
  altered RPC deadline/priority semantics. On timeout, SIGINT allows collector
  ownership cleanup, then a bounded hard-stop fallback; no remaining cells run.
- Standard job timeout45min leaves headroom for the fixed matrix and artifact upload.
  All normal success/error/timeout paths snapshot raw evidence; upload uses
  `if:always()`. Catastrophic runner loss or whole-job cancellation can still
  prevent upload and must be recorded as missing evidence, never success.
- Failed-stage bundle retains partial archives, source/generated files, actual
  runtime/binaries and logs. Reproducible SDK/package/CLI caches and .git internals
  are excluded; no raw probe output is excluded. Partial trace/decoder failures
  are preserved. No local source files, old artifacts or branches are deleted.
- Contents/actions permissions are read-only, checkout credentials not persisted;
  no paid/larger/self-hosted runner, environment approval policy or security change.
  Standard hosted private-repository minutes/storage may still be billed by the
  existing account; this proposal does not promise they are universally free.

Python3.12 is selected using a pinned standard setup-python action, avoiding
runner-default Python drift. Runtime/SDK version/SHA512 pins remain the original
native artifact lock. All invoked actions are pinned to immutable commits.

## Evidence gate, not a performance shortcut

Successful prepare means compilable bytes + reviewable receipts. Successful
collect means completed raw diagnostic capture, **not** correct Tier1 mapping,
zero fence cost, ARM weak-memory proof, or a QPS/P99 win. Inlining gaps, branch
frequency, kernel-scheduling absence and old Tier0 evidence retain their original
limitations. Native/decoder output still requires independent interpretation.
Later microcost and same-load, same-runtime **published official2.0.3 O/D/C**
end-to-end performance runs need separate review and jointly measured targets.
No result is synthesized by joining one microbenchmark's QPS to another's P99.

Primary workflow references:
[GitHub event rules](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows),
[cross-run artifact download](https://github.com/actions/download-artifact),
[setup-python version selection](https://github.com/actions/setup-python),
[verified setup-python v6.1.0 commit](https://github.com/actions/setup-python/commit/83679a892e2d95755f2dac6acb0bfd1e9ac5d548).
