# Producer ownership cost evidence

This authored kit retains the producer-reservation replay and adds an explicitly separate integration comparison. It does not import local build outputs or old research archives.

## Integrated profile

The current workflow passes `--profile integrated`. Its baseline is the previous
maintenance head `92a8936eb6656d6bf427ec4fbf807dde5903434a` and its candidate is the
exact invoking commit with the combined source tree pinned in
`eng/maintenance-integration-sources.json`. Both arms already retain outgoing
producer reservations. This comparison answers only the incremental effect of
integrating current dev and physical ownership into that maintenance head.

Feature acceptance instead requires **cc7 dev / ac17 prerequisites / combined
candidate**. The maintenance kits expose the two corresponding integrated profiles;
this producer comparison cannot replace either feature-cost comparison. The original
c603 producer-fix campaign remains available through the default `historical` profile,
with its original expected source trees and results unchanged.

```sh
python3 prepare_arms.py --repo "$REPOSITORY" --candidate "$CANDIDATE_FULL_SHA" --profile integrated
```

## Historical immutable source binding

- Public baseline: `c60393508cecd20886e798a474f189f4faf52c62`, production `src` tree `c242f33f0041439b176c43460864a888237b79d9`.
- Candidate: the exact invoking workflow `GITHUB_SHA`; its production `src` tree must be `3bf0a1eb6b162016d07f9d9329b1b3ca560f68a3`.
- This tree differs from the previously measured `8915e61e5534f6e47a5d679bfbea5c0dcb812eaa` only in the cold server Stop absolute-budget adapter. The current workflow rebuilds and measures both complete source arms; the earlier local and f118 samples remain separately bound to 8915.
- The preparer archives `src`, `Directory.Build.props`, `Directory.Packages.props` and `global.json`, records file hashes, refuses mismatched source trees and refuses existing destinations. No patch is applied relative to an enclosing repository. Failures write `source-provenance.json` before raising.

From this directory, with both commits already fetched:

```sh
python3 -m unittest test_prepare_arms.py -v
python3 prepare_arms.py --repo "$REPOSITORY" --candidate "$CANDIDATE_FULL_SHA"
for kit in shapes retry; do
  (cd "$kit" && python3 capture_build_toolchain.py && bash build_arms.sh baseline candidate-final)
done
```

Build both arms with official SDK 10.0.102. Then select an isolated official .NET runtime installation with one `host/fxr/*/libhostfxr.so`, set `DOTNET_ROOT` and `PATH`, and run:

```sh
for kit in shapes retry; do
  python3 "$kit/run_probe.py" --run-name "runtime-$RUNTIME" --iterations 5000 --core-only
done
python3 summarize.py --group "runtime-$RUNTIME" --expected-runtime "$RUNTIME" --expected-head "$CANDIDATE_FULL_SHA"
```

The reusable `.github/workflows/producer-hot-evidence.yml` performs these steps for 10.0.2 and 10.0.13. The main validation workflow must call it:

```yaml
  producer-cost-evidence:
    uses: ./.github/workflows/producer-hot-evidence.yml
```

## Fresh hosted evidence

Each runtime job launches 16 primary workload processes: two arms × four JIT modes × two policy contexts. The shapes context uses real authenticated TLS, two persistent sessions, populated maintenance owners and one successful update in **each** arm. It exercises 30,000 unary calls and 600 calls each for client streaming, server streaming, duplex, one-way client streaming and health. The retry context separately exercises 30,000 idempotent ordinary successes with retries configured and maintenance disabled. It does not test a failed retry.

The four JIT modes are forced FullOpts, retained Tier0, non-PGO Tier1 and tiered PGO with accelerated promotion (delay 0, threshold 30). Settings and observed tiers are recorded. Summarization requires the requested actual runtime, SDK 10.0.102, exact source/binary hashes, unchanged pool/session identities, successful update semantics, nine ordinary layouts and ten selection IL/local signatures. It requires complete native bodies for 60 paired target/mode/tier rows per runtime. Missing or inlined targets are not treated as zero cost. Native frame/instruction equality is **not** a pass gate.

Artifacts are named `producer-cost-<runtime>-<exact-head>` and contain fresh provenance, metadata, JSON call outcomes, native listings, build-toolchain hashes and `summary-runtime-<runtime>.json`. They do not contain archived source trees or build binaries. Failure provenance is retained by the always-upload step.

Shape allocation windows use `GC.GetTotalAllocatedBytes(precise:true)`. All six 100-call blocks are retained, including first and post-update blocks. These are whole-process allocations, not isolated invoker cost; one-way work may overlap a subsequent health window. No elapsed-time data or throughput-equivalence budget is produced.

## Historical local findings

`RESULTS.md` and `EXECUTIVE.md` describe the completed local c242→8915 campaign. Its 40 launches include eight narrow recaptures beyond the 32-launch hosted recipe. References there to raw campaign folders/JSON name retained **local** measurements, not committed files. Fresh hosted results must be read from their own exact-head artifacts; do not reuse local numeric samples as hosted outcomes. The older coordinator c242 evidence remains historical and separate.

## Fixed registration-native companion

The 16ef integration run on runtime 10.0.2 completed the functional shape workload
but emitted no standalone Tier1 `OnRegistered` body for the shapes baseline.
Its Tier0 and Instrumented Tier0 bodies are retained. The artifact does not decide
whether promotion or inlining explains the absent body. Runtime 10.0.13 and the
retry context cannot fill that evidence gap.

Each hosted runtime now declares one additional **paired shapes PGO campaign**,
`registration-runtime-<version>`, before either arm runs. Both arms execute the same
5,000-iteration phases, TLS update, shape checks and allocation windows using the
same binaries and JIT settings as the primary shapes campaign. Only the disassembly
filter narrows to `PendingRequestTable:OnRegistered`. There is no conditional
retry, repeat-until-complete loop, runtime substitution or product annotation.

The summarizer requires matching source, binaries, authored probe inputs, runtime,
PGO settings and workload, successful metadata/functional exits, and **both complete
Tier1 bodies in the companion itself**. An absent or incomplete body still fails,
even when the primary happened to provide it. `PrimaryNativeGaps` keeps the primary
absence visible; every native row names its source group. Companion samples remain
in `RegistrationNativeCompanions`, not pooled into the primary allocation series.
The original 16 launches and 60 paired tier-coverage requirements remain; the two
companion launches are recorded separately. Narrowing output is a collection change,
not proof of performance equivalence. Fresh Linux x64 jobs must establish whether
this fixed capture actually supplies the required bodies on both official runtimes.
