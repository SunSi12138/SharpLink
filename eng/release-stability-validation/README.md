# Frozen 3.0 stability gate (validation only)

This branch is dormant without `request.json`. Independently review the exact
remote tools HEAD before adding one request commit (sole parent = reviewed HEAD):

```json
{"source_sha":"e8fcef3ddb93d76457b690dadc279e7a8f91b47d","source_tree":"23074b603a369fe339b9958dec2da53a04b4bfa8","tools_sha":"<independently reviewed remote tools HEAD>"}
```

The request targets `validation/3.0-release-stability-20261030`; it is not included
here. Candidate branch/source/scripts are untouched. No dispatch connector is
available and existing `gh` is unauthenticated; no credential is created. This
push-request entry selects only the original Nightly release jobs, not its full
suite, two-hour chaos campaign, or a new performance comparison.

## Workload unchanged

Exactly three hosted Ubuntu jobs execute original scripts: 10-minute TCP,
10-minute SharedMemory, and 600-second Generated ABI Dynamic Module Churn.
Original budgets remain 30/30/135 minutes. SDK setup is the original composite;
its root `global.json` is byte-identical to the frozen source because only the
five enumerated validation files may differ. No workload, concurrency, restart,
fault-injection, gate, test argument, runtime policy, or duration changes. No
pre-build: the original runner performs its sole build. The ABI loop completes
its last full nine-process round even beyond 600 seconds. Original teardown and
resource assertions remain unchanged. No retry, re-run, tag or release.

## Transparent recording and evidence acceptance

The root explicitly requires actual pre/post execution byte binding. Setup
resolves the already-installed original `dotnet` executable; no SDK is upgraded
or newly pinned. A temporary PATH symlink records original command arguments,
working directory and exit status, and invokes that same executable. Child PATH
is restored to the original value so nested SDK commands do not use the shim;
the original script shell retains it for subsequent recorded invocations. Stdio
is inherited unchanged, including discovery JSON and redirected test logs.
Nonmatching commands are forwarded, never accepted as workload proof. Original
nonzero shell exit is preserved even if evidence recording also fails. Evidence
failure after original success is separately labeled `STABILITY_EVIDENCE_ERROR`
and exits 86, never a stability pass.

Immediately before each original no-build discovery/workload launch, the complete
required input inventory is nonempty and hashed; the first real post-build set
is frozen. Every subsequent invocation must match it, and post-execution bytes
must match. Source SHA/tree and tracked cleanliness are checked too. This avoids
a second build and known generated-source order changes between builds.

The selected nine ABI cases all use the default `PluginBundle.Load` path in
`RuntimeAssemblyIntegrationTests.Support.cs`: actual collectible input is
`test/SharpLink.DynamicServices/bin/Release/net10.0`, including both
`SharpLink.DynamicPlugin.Contracts.dll` and `SharpLink.DynamicPlugin.Services.dll`.
Default-context dependencies come from IntegrationTests output; unresolved
plugin dependencies come from the same DynamicServices directory. Hash all DLLs,
runtimeconfigs and deps in both roots, plus DynamicContracts, ReplacementServices
and ReplacementContracts build outputs. The latter replacement roots are built
inputs, **not claimed executed replacement variants**; the selected cases never
set `replacement:true`. Required main/runtime/product/plugin DLLs and applicable
runtimeconfig/deps must be present and nonempty, not merely a nonempty directory.

For ABI, each real test invocation retains its own official .NET host startup
trace. Only a CoreCLR selection following the IntegrationTests runtimeconfig
marker proves actual test runtime; an SDK host runtime is insufficient. This is
startup host tracing, not a collector/profiler or runtime roll-forward override.
Reference: https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-environment-variables#dotnet_host_trace

After original success, a separate fail-closed verifier requires original reports
and command ledger: Chaos final Passed/Completed, exact options, original coverage,
no unexpected/log errors, final server stop and all five drain metrics zero;
ABI exact selected UIDs in complete nine-process rounds, counts/600s summary,
successful exits and actual application runtime traces. Installed `dotnet --info`
is retained separately, never confused with actual process runtime. Chaos's
original duration timer starts before warmup while ActualElapsedSeconds starts
after; record both clocks rather than impose a new workload-clock threshold.
A ten-minute result **does not cover the six-hour retained-memory window**.

Failed samples, original logs/reports/dumps, pre/post hashes, ledger and tool errors
remain uploaded with `always()` and three-day retention. Artifact existence is
not acceptance. Token is `contents: read` only; no evidence repository or extra
permissions. Local checks use pure Python/fake-dotnet fixtures only, never product
MTP execution/discovery or either soak.
