# Cooperative-maintenance enabled-owner evidence

This standalone probe uses real TLS, application authentication, exactly two persistent physical sessions, and one held generated proxy. Both arms make 15,000 ordinary calls before and 15,000 after a phase boundary. Only the feature arm requests one compatible implementation replacement. Identical calls return 30 before replacement and 31 afterward; the prerequisite remains at 30. All four capture points verify the exact same connection/session objects, TLS, authenticated cohort, and Ready state. Feature captures additionally require populated enrollment and negotiated maintenance capability.

This is functional, layout, IL, and native evidence. It has no throughput score, latency threshold, or whole-RPC allocation claim. The separate parser allocation microprobe uses a prepared Ready session, not the enrolled TLS cohort. Native output spans the whole process, not separately timed pre/post phases. Read actual emitted tier labels rather than assuming promotion from a launch name. Preserve every listing, failure, and residual difference. A larger frame or code body is not a throughput measurement. Native stack extraction currently recognizes x64 `rsp`; an empty extraction on another architecture is not a zero-byte frame.

## Integrated source profiles

The current workflow uses `--profile integrated-prerequisites`: immutable `ac17c8d8`
(dev plus physical ownership) versus the exact combined candidate. For the other
feature-acceptance arm, use `--profile integrated-dev` against `cc7c458b` in a
separate fresh probe checkout. Together these retain the three-arm comparison
**dev / prerequisites / candidate**; the two pairwise campaigns must not be
combined across different environments as if they were one paired experiment.
`eng/maintenance-integration-sources.json` pins the full commits and source trees.
Both integrated profiles retain #930 and require identical invocation dispatch.
`published-prerequisites` is the existing baseline directory label; the profile
and exact baseline identity in `source-provenance.json` determine its meaning.

```sh
python3 prepare_public_arms.py --repo "$GITHUB_WORKSPACE" --candidate "$GITHUB_SHA" --profile integrated-prerequisites
```

Preparation archives source only. It does not run performance samples. Historical
results are unchanged. The default `historical` profile below retains the original
#928/#931/#932 reconstruction and its independent source manifest.

## Reproduce the historical profile on a hosted Linux x64 runner

Prerequisites: Python 3.12+, Git, the selected .NET 10 SDK, and the intended official runtime hosts. Build both arms with one SDK, then run the identical assemblies with each runtime host. The local reference campaign uses SDK 10.0.102 and runtimes 10.0.2/10.0.13. Provisioning the official runtime hosts belongs to the caller's workflow; this probe does not install software or change the SDK.

1. Check out the feature with full history. Fetch these exact prerequisite commits if they are absent:
   - #928: `37b92f20e20ce8a7b751660d56d636a0ce1660e7`
   - #931: `b488fe43aec90962adb2e5a6bcca04153d142092`
   - #932: `f4be372ae4b013c767eb7f5eb0e43aad1cdb41fe`
2. From this probe directory, run:
   ```sh
   python prepare_public_arms.py --repo "$GITHUB_WORKSPACE" --candidate "$GITHUB_SHA"
   python capture_build_toolchain.py
   bash build_arms.sh published-prerequisites candidate
   ```
   The prerequisite starts at #931 and applies only the #928-to-#932 `src` diff. Its complete production/build-file manifest must match `expected-prerequisite-sources.json`. The candidate's invocation-dispatch source must remain identical. No repository test project is copied or invoked.
3. Select a runtime host in PATH and DOTNET_ROOT, then run:
   ```sh
   python run_probe.py --run-name runtime-10.0.2 --iterations 5000
   python analyze.py --group runtime-10.0.2
   python summarize.py --group runtime-10.0.2
   ```
   Repeat with the other runtime host and a distinct run name. Each arm is launched in FullOpts, Tier0, non-PGO Tier1, and tiered-PGO modes with accelerated promotion. Actual Tier0, Instrumented Tier0, Tier1 and FullOpts versions remain in native listings. The PGO launch sets call-counting delay to zero and threshold to 30; it is not the wholly default runtime configuration.
4. Retain only the authored probe plus fresh job evidence: `source-provenance.json`, `evidence/build-toolchain.json`, build SDK version, each run's `provenance.json`, metadata/outcome JSON, native listings, `compact-metrics.json`, and the corresponding `comparison-*` directory. Do not substitute a prebuilt local research bundle.

The probe records actual runtime version, process architecture, CoreLib/CLR/JIT/hostpolicy/hostfxr/host hashes, subject DLL hashes, source hashes, and explicit JIT settings. It does not dump the process environment. Baseline/candidate use the same transport configuration, service bytes, call inputs, pool count, SDK and runtime mode. Their maintenance API state intentionally differs. The build-isolation props prevent an enclosing repository's packaging properties from silently changing this standalone probe.

The service contract and implementation are independently authored. The same service DLL is loaded into two collectible contexts sharing the exact contract assembly; replacement-only static state supplies the behavioral witness. The small service's constructor/disposal counters are outside its ordinary Add method.

The hosted job installs the official 10.0.102 and 10.0.113 SDKs with the repository-pinned setup action. A probe-local global.json pins SDK10.0.102 with roll-forward disabled; both arms use it and the actual version/compiler file hashes are recorded. The archived subject global.json files are not modified. Each run then uses an isolated copy of the selected installed runtime/fxr directory plus the common installed dotnet host, and requires the measured RuntimeVersion to equal 10.0.2 or 10.0.13. The host is hashed independently; do not infer its version from the runtime directory. Fresh job results, not local binaries, are uploaded.

The summary requires all eight inspected ordinary object layouts/offsets and all eight inspected connection-selection canonical IL/local signatures to match the published prerequisites. These are narrow structural regression guards, not a whole-runtime or throughput assertion. Other native differences, including profile-dependent dispatcher layouts and the server reader stack reservation, remain visible for review.

Native comparisons intersect matching emitted method versions. The complete version inventory remains in native-summary.json; a missing standalone Tier1 body is not an equality result. No native-equality or throughput threshold is imposed.

Each launch must also contain a nonempty native listing and actually observed requested compilation tiers: FullOpts, Tier0, Tier1 (including OSR), or both Instrumented Tier0 and Tier1 for PGO. These are evidence-completeness checks, not a requirement that every method has a separately emitted body. An inlined or absent individual method is never reported as zero cost. `python test_native_evidence.py` checks the empty-listing and missing-tier counterexamples.
