# Recompute Issue #735 evidence

These Python standard-library programs recompute the pinned historical artifacts
used by [the audit](../../../docs/research/issue-735-production-architecture/failures-and-evidence.zh-CN.md).
They do not build or run a production scheduler, edit a collector, reclassify failed
tests, or establish a performance result against current dev.

## Inputs and commands

Use the extracted evidence archive's `evidence/historical` directory as
`--evidence-root`, and a separate directory for generated reports. Both paths are
explicit; no particular workspace location or local Git checkout is required.
Python must run without `-O`, because assertions check the frozen inputs.

```bash
python3 eng/issue735/analysis/recompute_historical.py \
  --evidence-root /absolute/extracted/evidence/historical \
  --output-dir /absolute/recomputed
python3 eng/issue735/analysis/recompute_phase_ab.py \
  --evidence-root /absolute/extracted/evidence/historical \
  --output-dir /absolute/recomputed
```

Each input directory preserves the original ZIP layout. The audit records the
downloaded ZIP sizes, SHA256 digests, fixed source identities and relevant CI logs.
Large ZIPs remain in the evidence archive and GitHub Actions artifacts, outside Git.

| Input directory | Original artifact | Evidence layer |
|---|---|---|
| `performance-39f27f08/` | [11703138985](https://github.com/SunSi12138/SharpLink/actions/runs/38140928821/artifacts/11703138985) | Actual generated RPC, JIT PGO ON; old dev `0fe26024` versus `39f27f08` |
| `stress-8ac4c849/` | [11703033512](https://github.com/SunSi12138/SharpLink/actions/runs/38142405753/artifacts/11703033512) | Eight original full test runs; rounds 1, 3 and 5 failed |
| `b3_jit/` | [10816087303](https://github.com/SunSi12138/SharpLink/actions/runs/36018718001/artifacts/10816087303) | Matched transport, 48 JIT processes / 768 samples |
| `b3_native/` | [10815623160](https://github.com/SunSi12138/SharpLink/actions/runs/36018718001/artifacts/10815623160) | Matched transport, 8 actually executed NativeAOT processes / 128 samples |
| `phase_a/` | [10751542617](https://github.com/SunSi12138/SharpLink/actions/runs/35862800731/artifacts/10751542617) | Phase A microkernel, separate real RPC and explicit-window load controls |
| `b2_model/` | [10765897507](https://github.com/SunSi12138/SharpLink/actions/runs/35893121186/artifacts/10765897507) | B0 full-controller and B1/B2/publication models |

## Checks and output

`recompute_historical.py` checks all 416 generated-RPC records, source/mode labels,
collector completion/error state, validation outcomes, four arms and four rounds
per cell. It retains all 26 cells and paired deltas, including A/A controls. The
prototype path checks process exit codes, measured source tree, runtime kind,
received/returned totals, queue remainder and rejection counts for all 896 samples.
Its sensitivity output is explicitly restricted to the matched 8 KiB prepared-cap
subset; it does not imply that subset is complete production acceptance. The stress
output preserves the exact original eight exit codes and failure messages.

Outputs are `production-recomputed.json`, `production-recomputed.md`,
`prototype-recomputed.json`, `prototype-recomputed.md`, `stress-recomputed.json` and
`historical-analysis-provenance.json`.

`recompute_phase_ab.py` checks 1,624 microkernel rows, 84 real RPC records, 24 c8
load-test records containing 96 operation results, and 624 Phase B model rows.
It keeps each family's original aggregation rule separate. The frozen Phase B
validator is embedded with its fixed source URL and original file SHA256; its body
is unchanged apart from the function name. No import from a mutable historical
checkout or summary-writing entry point is needed.

Outputs are `phase-a-recomputed.json`, `phase-b-model-recomputed.json` and
`phase-ab-analysis-provenance.json`. Each provenance file records the executing
script's SHA256 and the resolved input/output directories.

Both delivered programs were actually executed against the downloaded archive,
exited 0, and reproduced all seven numerical reports byte-for-byte. These are
recomputations, not new measurements or newly built NativeAOT results. The separate
historical attribution workflows and all their original red outcomes are documented
in audit section 7; these scripts neither rerun nor reinterpret those workflows.

## D generated-RPC screen: independent recomputation

[`recompute_d_screen_independent.py`](recompute_d_screen_independent.py) is a separate
standard-library reader for the two D screening runs. It never imports the primary
analyzer or invokes dotnet. Point it at **one** extracted run directory containing
`nativeaot/`, `jit-pgo0/` and `jit-pgo1/`. Keep the output outside that input tree.

```bash
python3 eng/issue735/analysis/recompute_d_screen_independent.py \
  --input /absolute/extracted/evidence/new/38164814650 \
  --output /absolute/recomputed/d-v1.json
python3 eng/issue735/analysis/recompute_d_screen_independent.py \
  --input /absolute/extracted/evidence/new/38165237528 \
  --output /absolute/recomputed/d-v2.json
```

Each command checks all 432 original reports, binaries, compile/source identities,
per-RPC timestamps and nearest-rank quantiles. The delivered CLI additionally
rejects missing planned positions, nonzero process exits and unsuccessful
collections. It produces per-runtime, per-cell original four-pair metrics and
separate A/A comparisons. V2 completed and settled metrics are both retained;
unavailable V1 settled metadata is explicit null, never inferred from V2.

The `.provenance.json` sidecar records the executing script, input metadata and
output SHA256. Both commands were actually run at delivery and exited 0. All
original numerical and identity values matched the prior independent review;
only the resolved input path and three explicit-null metadata fields per V1
report differed in representation. The runs remain separate measurement hashes;
864 successful reports are not one pooled population or a production Go.

The companion primary analyzer has a **single runtime artifact** input:

```bash
python3 eng/issue735/analysis/recompute_rpc_screen.py \
  --input /absolute/extracted/evidence/new/38165237528/nativeaot \
  --output /absolute/recomputed/v2-nativeaot-primary.json
```

It performs additional plan, runtime-assembly, flags and workload checks. The
independent reader does not call its functions. Source/binary/ZIP identities,
including recovery of two incomplete local V1 ZIP files with 2,182 identical
original entries, are in the [D artifact index](../../../docs/research/issue-735-production-architecture/directional-artifact-index.json).
See the [methods and No-Go report](../../../docs/research/issue-735-production-architecture/directional-control-and-methods.zh-CN.md)
and [complete independent tables](../../../docs/research/issue-735-production-architecture/directional-independent-review.zh-CN.md)
for completed-versus-settled scope, positive signals, negative outliers, A/A limits
and the independent baseline lease defect. No B2 production pilot was executed.

The delivered primary reader was also rerun separately on all six runtime
artifacts: all commands exited 0, 864 successful reports passed stricter identity
checks, and all 35,752 pre-existing numeric values and paired raw arrays were
unchanged. Thirteen synthetic analyzer cases preserve expected red/green outcomes
for missing arms, failed/not-run processes, and identity/measurement mismatches.
These are analyzer checks, not production tests or extra performance samples.
Evidence: `evidence/new/analysis-recheck/{numeric-equivalence.json,guard-red-green-results.json,frozen-manifest.json}`.
