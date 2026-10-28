# #931 R1 actual-TCP native-only plan

Frozen before execution, 2026-10-28. This is a mechanism probe, **not timing or
performance acceptance**. #931 original NoGo remains. No product changes here.
Independent review of this tool's exact HEAD is required before execution.

## Trigger and single variable

The independent run40646151477 review found the ac17 actual-TCP Tier1 intercepted
iterator tests `hasNext` at IG13/0x018E, loads Current, jumps to IG65/0x0851 and
retests the same r15d. This supports decision-tree R1, not a causal tail claim.
Reuse existing R1 product `94adeae886c4ddfd0c02ec2dcb700cd97e9cad80`:
its parent is `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`, and the complete diff
is four added/four removed lines in SharpLinkClient.Interceptors.cs. No other
optimization, management hotpath, owner/CTS/ALC change, helper or await is added.

Both arms reuse exact common ac17 DLLs and the exact reviewed probe/module from
archived run40646151477's client-prepared/candidate snapshot. Baseline uses its
original ac17 Client; candidate replaces **only Client.dll** with Release output
built from clean exact R1 using SDK10.0.102. Matching Client PDB follows that DLL.
Runtimeconfig is explicitly pinned 10.0.13 / rollForward Disable. Source versions,
loaded paths and every DLL hash must match; missing provenance fails closed.
Existing preparation is reused rather than rebuilding unrelated common binaries.

## Fixed native experiment

- Linux x64, private runtime-only Microsoft.NETCore.App + AspNetCore.App10.0.13.
- Runtime archives downloaded from Microsoft's official release-metadata URLs;
  SHA512 verified against retained official releases.json before extraction.
- Two first allowed logical CPUs, topology saved. Prefer sysfs; only when sysfs
  is entirely absent, require explicit unique processor/package/core IDs for every
  allowed CPU in /proc/cpuinfo and save the raw file. Partial/malformed topology
  still fails. Procfs IDs are container-reported, not independently verified SMT.
  This is shared cloud hardware,
  not a claim of dedicated cores or matching the old GitHub physical machine.
- Unchanged tool51f5bd0e actual TCP probe, dynamic intercepted stream100,
  one worker, exactly one ready connection, default tiering/PGO, overrides cleared.
- Baseline then candidate, one process each. Exactly4352 completed RPCs including
  the traced first RPC. Fixed250ms pauses after1024/2048/3072/4096 completions.
- No adaptive extra warmup, retry, timing, QPS/latency sample or timing branch.
  Host timeout90s per arm is a safety bound, not a timing observation for comparison.
- Attempt each predeclared arm once even if the first fails; preserve exit/status,
  stdout, native including Tier0/instrumented bodies, proof, hashes and command.
- Four actual closed-generic targets must have positive-size Tier1/Tier1-OSR:
  intercepted iterator, dynamic request iterator, Runtime pump, Server bridge.
  Missing optimized body is a failed gate, never zero and never replaced by Tier0.

Interpret only exact native listings after both identity/native gates pass.
Compare the intercepted success path's `hasNext` branch before Current and
before yield, native body bytes/frame/EH where available. Retain all four bodies;
common binary identity does not guarantee identical adaptive PGO machine code.
If duplicate-test shape disappears, report narrow mechanism observed only.
If unchanged/missing, preserve that finding and return to the decision tree.
Neither result authorizes timing, merging or release, nor proves every call in
any separate timed process has reached Tier1.

## Correctness reuse and applicability

Existing exact R1 has67 focused Unit and5 Integration successes (macOS ARM64,
SDK10.0.102/runtime10.0.11); report87f76fb changes documentation only. No test or
product source has changed since those gates. Reuse as exact-source focused
correctness evidence, not as Linux10.0.13 coverage or full release gates.
Current/false-exit catch boundaries and natural-exit lowering remain the independent
product-review concern already documented in the existing loop-exit report.
The current tool does not rerun correctness or claim new inventory coverage.
The installed/repository dotnet-test:run-tests skill is absent; any separately
needed .NET tests must use native MTP `--treenode-filter`, not VSTest `--filter`.

## Invocation after independent PASS

```
python3 eng/physical-ownership-probes/r1_native.py \
  --reference-evidence /absolute/path/to/archived/ownership-931-evidence \
  --r1-source /absolute/path/to/clean-94adeae8 \
  --build-dotnet /absolute/path/to/sdk10.0.102/dotnet \
  --runtime-dotnet /absolute/path/to/private10.0.13/dotnet \
  --output /absolute/new/nonexisting/evidence-directory
```

Logs/build failures are retained. A restore/build failure before either probe is
an infrastructure/preparation blocker; diagnose it rather than fabricating a run.
No workflow modification, publication, merge or tag is part of this tool change.

## Preparation attempt001 (preserved)

Tool573c513 stopped before builds or runtime probes: this cloud does not mount
/sys/devices/system/cpu, and inherited cpu_topology plus lscpu require it.
No sample executed. The next reviewed tool permits a strict procfs identity
fallback only for entirely absent sysfs; workloads, affinity requirement,
private runtime, identity/native gates and fixed operation counts are unchanged.
This is a preparation fix, not a retry of a failed native/performance sample.

## Hosted environment validation (one new, explicitly distinct environment)

The fixed cloud attempt003 completed both4352-RPC processes, but each omitted
Runtime-pump/Server-bridge Tier1. Intercepted and dynamic Tier1 existed. The
complete four-target gate remains Failed. Do not retry this unchanged cloud run.

Hypothesis to test, not established cause: default adaptive promotion opportunities
vary with the cloud's guest/kernel/CPU scheduling. The cloud reported AMD EPYC9V74
and lacked sysfs; original run40646151477 used ubuntu24.04/AMD EPYC7763 and captured
all four targets with the same4352 operations and pauses. The hosted check changes
only execution environment; it does not add warmup, force tiering or relax gates.

A separately reviewed `.github/workflows/ownership-r1-native.yml` is triggered
once by opening a Draft **DO NOT MERGE** validation PR from
`codex/931-r1-native-validation` to the existing `codex/931-client-ablation-tools`
base frozen at51f5bd0ec3f600196f2e26ea48a466ec2cea62d8 in this repository.
It deliberately excludes synchronize/reopened/manual dispatch triggers. Root must
publish the reviewed exact source, independently verify its remote HEAD, then open
that PR once; later report pushes do not start new samples. No automatic retry.

The workflow checks out exact PR head (not merge SHA), exact R1 product94adeae8,
and evidence commit5a6cf6c490b43c083fb772e2c6f2663fa94bbe16. The archived931-pgo-12
SHA256 is34210a62a927ac8e3c9168c8a3e9e4d884998fad4826fc8c94895b09f281afe7.
.NET/ASP.NET10.0.13 archives are verified against fixed official SHA512 values;
the build SDK remains10.0.102. Actions are commit-pinned, contents-read-only, and
checkout credentials are not persisted. All build/native failures and raw proofs
are uploaded with exact toolSHA/runID/attempt; prior cloud failures remain part of
the evidence and are not overwritten by any hosted result.

Two native-only processes, the same fixed order/4352RPC/pauses, all four exact
closed-generic optimized bodies, and no timing path remain mandatory. Another
incomplete gate means stop and report that fixed plan inconclusive on this host;
not a reason to rerun, tune thresholds, add samples or call R1 accepted.


Publication route correction before execution: do not target dev. The original
tool ancestry includes unaccepted product changes and conflicts with current dev;
GitHub does not run pull_request workflows on conflicting PRs. Targeting the exact
existing51f5 tool branch gives a clean tools/workflow/docs-only diff, avoids the
dev-specific inherited product/performance workflows and does not create a product
merge candidate. Root verifies the base remains51f5 before opening once. The
unfiltered pr-fast workflow may still run; do not describe all other CI as suppressed.
This PR is Draft, validation-only and **DO NOT MERGE**, including into the tool base.

GitHub's [pull_request event documentation](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request)
permits filtering explicit target/base branches, not only the default branch;
the job intentionally checks out the reviewed head SHA rather than the default
merge-ref checkout. No pull_request_target or credential/permission change is used.
