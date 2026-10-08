# Directional gate acceptance preflight

This is a disposable CI harness. It does not update PR #742 or declare it Ready.
No existing workflow, path filter, or skip marker is changed.

## Exact sources

- **D**: unchanged dev runtime `0fe26024b114bb6e78411a9b86276086c045d03d`.
- **N**: archived integrated redesign tree `4c5943fec3a85089cefa8a1b6dc8c0f6502567ce`.
- **H**: N plus directional-gate patch, tree `522585079b97c99f7986eb50f3ed6c69d4086bfb`.
- Gate patch SHA-256: `02490bd5382b4d7bc7ff50e9a578bae874ae1d8dd9b81ca0e0dc0aae64a5ced9`.

`prepare.py` first invokes the existing pinned redesign reconstruction from
`1082dec14b9d0e3bbe43a823227e69d80a808069` at recipe
`06b737df0d9aab66caed9c446a572b3a8ce4efae`. It verifies the archived N tree,
creates H independently at that recipe commit, applies N's complete candidate
patch, verifies N again, and only then applies the directional patch. Every
reconstruction, source blob, complete patch and incremental patch is identified
in retained provenance. The e834+38 local overlay tree `d5f55599...` is never
substituted for the archived N full tree: its 52 harness-only differences are
not a production-source difference but would change a full-tree identity.

The new patch includes a design ADR and a maintainability rationale update.
All parsed maintainability data except descriptive `reason` fields must remain
identical, including every numeric budget. The patch itself is digest-pinned.

As in the original acceptance, D receives identical copies of the two existing
measurement harness files. Its runtime stays at unchanged dev. The actual
NativeAOT RPC host uses the original digest-pinned
`eng/preflight-735-prepare-native-rpc.py`: only out-of-measurement JSON is adapted
to generated metadata, and the unchanged timed-loop hash is retained.

## Jobs and retained populations

1. **Correctness**: old-N portable directional-progress negative controls,
   isolated N/H constructor-cost evidence, exact-H build, whitespace formatter,
   reference boundaries, unchanged maintainability budgets, all default tests,
   original ownership/wrapper negative controls, source restoration, original
   deterministic allocation gate, original ready-writer source checks,
   171/171 writer checks, and all experimental runtime tests.
2. **Production acceptance**: original `production-acceptance` job from
   `.github/workflows/735-return-hint-preflight.yml`. Every existing build,
   measurement, counter-attribution, summary and validation step is unchanged.
   Only source preparation/provenance and artifact naming differ, and two
   supplemental raw-validation calls are added. There are six JIT micro launches
   (PGO 0/1 x three repetitions), one NativeAOT micro launch, 84 original paired
   TCP/SHM RPC reports, and 24 original paired c8 reports. The original 7 RPC
   scenarios, AB/BA/AB order, reverse second-round scenario order, 30 warmups,
   5-second measurement, 200000-operation cap, c8 sizes 1/10000, 8 KiB/64 KiB
   windows, 1-second warmup, 2-second duration, and recording-off mode remain.
3. **Actual NativeAOT RPC and writer**: identical D/H actual-native hosts,
   retained binary hashes, ELF information, symbols and rooted disassembly,
   the full original 84 RPC launches in the original order, an explicit
   `dynamicCodeSupported=False` assertion in every process, and actual-native
   171/171 writer checks on H after the original writer preparation.

All artifacts upload with `always()` and retain raw reports/logs. No missing
cell is replaced, retried, or dropped. Original operation-cap hits are disclosed
in supplemental validation without changing the original cap or acceptance
logic. No optional N/H throughput diagnostic delays the complete D/H evidence.

The micro validator checks all 232 explicitly enumerated cells, unique keys,
finite metrics, exact checksums, and **3 sequential / 6 contention repetitions**.
Contention reports include internal AB+BA samples and must not be rejected by
assuming three repetitions. `self-tests.py` exercises valid populations and
rejects wrong repetitions, checksums, missing/duplicate cells, nonfinite data,
wrong source identity, windows, duration, and recording mode.

## Interpretation

A green collection job is not a performance approval. The original production
job reports numeric deltas but does not encode numeric throughput acceptance
assertions. None have been invented, weakened or silently omitted here. Review
all original cells and regressions against the user's acceptance conditions
before publishing any readiness conclusion.

The extra directional Lock costs construction memory. The source author's
isolated .NET 10.0.2 x64 probe observed **408 -> 456 B/controller (+48 B)**,
including a 40-byte Lock and an 8-byte reference. CI measures its own eight
balanced independent processes using separately built/copied runtime arms and
checks every runtime hash. It reports that fixed cost without subtracting it
from production measurements. Steady-state operation allocation and mechanism
contention rows from this probe are diagnostic only.

## Local validation

Run `python3 eng/preflight-742-gates/self-tests.py` for portable validator tests.
Reconstruct into an unused absolute directory with
`python3 eng/preflight-742-gates/prepare.py DESTINATION --include-dev`.
Each invocation creates new disposable git worktrees and refuses existing paths.
No helper commits, pushes, changes existing workflow triggers, or marks the PR
Ready. Full hosted acceptance and actual-native builds must run before their
results can be claimed.
