# Unchanged shipping G2: full original acceptance coverage

This standalone two-job workflow fills missing TCP and actual NativeAOT coverage
for the exact already-screened G2 source. It changes no existing workflow,
measurement harness, product source, original validator, population, or numeric
threshold. Only the new workflow and this directory trigger it on the helper
branch; it is also manually dispatchable. Publication is a separate action.

## Fixed identities and prior evidence

- Helper base: `0b8af4aa31ab855b17af37da50f93b30f388f14e`.
- Dev: `0fe26024b114bb6e78411a9b86276086c045d03d`.
- Shipping G2 tree: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`.
- Complete G2 source patch SHA-256:
  `5217ed02fbeb260f68413fba7f4eac21eb197e37bdff31a612fb4973ccf5139b`.
- Original reconstruction and original common measurement harnesses are reused
  from `eng/preflight-742-quiescent/prepare.py`. The same-binary diagnostic
  adapter is never reconstructed, built, or measured by this workflow.
- Exact-source correctness is reused from
  [run 37786811781](https://github.com/SunSi12138/SharpLink/actions/runs/37786811781):
  2,128 default and 2,128 experimental tests passed. The prior SHM short/client
  regressions remain no-go evidence; collecting a new full matrix does not
  erase those results or establish readiness.

`integrity.py` rejects any inherited-file drift from the helper base, checks the
shipping G2 tree/full-patch identity and common dev/candidate harness bytes,
and archives the complete source reconstruction proofs. The JIT job hashes all
tracked source/test files and every actual benchmark output before and after
measurement, including its NativeAOT micro publish directory. The actual-native
runner independently hashes all tracked source, its generated minimal host,
and complete native RPC publish directories before and after all launches.

## Unchanged required populations

1. Original internal keyed/resolved micro: two PGO modes, three process repeats
   each, plus one actual-NativeAOT process; all seven reports contain all 232
   cells, with three sequential and six contention repetitions per cell.
   These compare API modes within G2, not dev/G2 runtime revisions.
2. Original production JIT RPC: all 84 launches, seven scenarios, TCP and SHM,
   three paired repeats, original AB/BA/AB and scenario ordering, 30 warmups,
   five seconds, and 200,000-operation cap.
3. Original c8: all 24 reports and 96 operation rows, both transports, sizes
   1/10,000, unary/c2s/s2c/duplex phase order, concurrency eight, 8,192/65,536-byte
   windows, one-second warmup, two-second measurement, recording off.
4. Actual NativeAOT RPC: the same full 84-launch original production population.
   Its unchanged JSON-only adaptation retains the measured-loop bytes and
   requires `dynamicCodeSupported=False` on every launch.
5. Original actual-NativeAOT 171 ready-writer checks. Their original disposable
   source hook is applied only after shipping-source native RPC measurement;
   its exact source delta and compiled native publish directory are retained.

Ten original production build/timing/summary bodies are byte-identical to the
reviewed dormant full-acceptance template. Original micro and strict population
validators run unchanged, including unary failures and completion accounting.
All raw repeats, cap flags, CPU/allocation data and failures remain artifacts.
There is no cell retry, exclusion, threshold waiver, automatic Ready action,
merge, or deployment. A successful collection still requires result review.

The former Callgrind/install step is omitted because it is optional attribution,
not a timed population or numeric acceptance gate. No profiler, tracer, security
change, external computer, or additional installation experiment is used.

## Retention and validation

Artifacts include actual NativeAOT RPC ELF publish directories and symbols for
both dev/G2, actual micro and writer ELF publish directories, tar archive hashes
and contents, source proofs, original timing logs and raw reports. Tars preserve
executable modes; duplicate raw micro/writer publish folders are excluded from
upload because their complete tars retain the same bytes. Writer publish files are hashed before and after its checks.
Artifact retention is 30 days, and upload runs even after failure.

Local preparation validation: exact shipping G2 and full patch reconstructed;
31 original portable validator controls pass; native JSON-only adaptation and
measured-loop identity verified; all shell/inline Python/YAML and unchanged-body
parity checked. No local SDK was installed, so builds and native execution are
explicitly pending GitHub Actions.
