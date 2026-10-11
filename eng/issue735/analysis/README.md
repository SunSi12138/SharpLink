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
and the independent baseline lease defect. These D populations contain no B2 code.
The later B2 pilot and its separately gated measurements have their own source
identities and evidence ledger; D results are never relabelled as B2 results.

The delivered primary reader was also rerun separately on all six runtime
artifacts: all commands exited 0, 864 successful reports passed stricter identity
checks, and all 35,752 pre-existing numeric values and paired raw arrays were
unchanged. Thirteen synthetic analyzer cases preserve expected red/green outcomes
for missing arms, failed/not-run processes, and identity/measurement mismatches.
These are analyzer checks, not production tests or extra performance samples.
Evidence: `evidence/new/analysis-recheck/{numeric-equivalence.json,guard-red-green-results.json,frozen-manifest.json}`.


## B2 fixed pilot: independent readers and retained partial populations

The B2 screen pins candidate `5cabd4d7cc5ce9aa0a87424e36954e414e0912da`
against common correctness control `c7ba1768f31ce7219dc438dc6009347de6806ed5`.
That is not the initial `f8ba8912` baseline used by D; the three common correctness
fixes are not counted as B2 performance gains. See the [methods and No-Go report](../../../docs/research/issue-735-production-architecture/b2-real-rpc-methods-and-results.zh-CN.md).

[`recompute_b2_screen.py`](recompute_b2_screen.py) is a byte-for-byte copy of the
frozen 5cab primary validator. It takes an extracted original RPC artifact root,
not its parent population folder. It checks the predeclared recipe, failures and
NotRun positions, measurement identity, actual binary/flags/payload, per-RPC
samples, fairness and activation evidence. An exit 0 means evidence was parsed
and classified correctly, **not** that the original JIT collection passed or that
production acceptance is complete.

```bash
python3 eng/issue735/analysis/recompute_b2_screen.py \
  --input /absolute/extracted/evidence/new/38173700315/nativeaot-sized-dto/raw \
  --output /absolute/recomputed/b2-native-sized-primary.json
python3 eng/issue735/analysis/recompute_b2_sized_independent.py \
  --input /absolute/extracted/evidence/new/38173700315/nativeaot-sized-dto/raw \
  --output /absolute/recomputed/b2-native-sized-independent.json
```

The separate [`recompute_b2_sized_independent.py`](recompute_b2_sized_independent.py)
reads raw reports and per-RPC timestamps without importing the primary reader or
using its metric summaries. The three actual same-source executions and the
independent Git/input checks are preserved; repeat the commands with
`jit-pgo0-sized-dto/raw` and `jit-pgo1-sized-dto/raw` to retain their original
48/1/95 and 49/1/94 success/failure/NotRun counts. Do not synthesize missing pairs,
call those partial results four-pair screens, or pool runtime populations.
Source/binary verification has additional frozen records in the evidence archive;
the small independent numeric reader alone is not their replacement.

The byte-array control has a separate independent reader, which also verifies
original ZIP bytes and fixed Git objects. Its `--repo` must point to a SharpLink
Git checkout containing both pinned commits and the corresponding measurement
objects; it does not silently fetch missing objects or call dotnet.

```bash
python3 eng/issue735/analysis/independent_b2_byte_array.py \
  --input /absolute/extracted/evidence/new/38173700315/jit-pgo1-byte-array \
  --output /absolute/recomputed/b2-byte-jit-pgo1 \
  --repo /absolute/SharpLink \
  --archive /absolute/extracted/evidence/new/38173700315/jit-pgo1-byte-array/rpc-screen-original-11715117995.zip
```

Use `jit-pgo0-byte-array` or `nativeaot-byte-array` for the other two controls;
their normal `rpc-screen.zip` is complete and `--archive` can be omitted. JIT1's
old incomplete `rpc-screen.zip` remains deliberately unaltered and must fail the
ZIP gate. The recovered original is the same GitHub artifact, and every one of
its 592 extracted original files matches byte for byte; this was not a new RPC
run. Numeric recomputation does not cure a corrupt original ZIP.

[`cross_check_b2_byte_array.py`](cross_check_b2_byte_array.py) only compares two
already derived result sets and imports neither reader. Its positional directory
must contain `independent-processes.json`, `independent-paired-comparisons.json`,
`independent-verification.json` and the separately produced
`strict-root-recomputed.json`; prefer a fresh output directory, rather than
changing frozen evidence. The actual three comparisons each matched 2,758 shared
numbers exactly. Byte-array counters are zero: B reads the actual B2 API, while A
has no B2 API and the shared adapter emits defaults. These are controls, not grant
activation or an observation that all original locks/queues performed zero work.

All six final populations together retain 648 planned positions, 457 successes,
2 runtime failures and 189 NotRun. The first 608 recipe's six compile failures
and 648 NotRun remain a different collection. Neither analyzer reruns Actions,
changes an assertion, or claims a full B2/B3 production Go.

## Preserve and restore the complete evidence archive

[`package_evidence.py`](package_evidence.py) preserves all supplied evidence,
including original and corrupt ZIPs, raw samples, failures, logs, source snapshots
and executed binaries. It never reads adjacent repositories or other conversations.
Use a separate output directory and wait until all collectors have stopped writing:

```bash
python3 eng/issue735/analysis/package_evidence.py pack \
  --input /absolute/current/evidence \
  --output /absolute/evidence-package \
  --part-mib 240
python3 eng/issue735/analysis/package_evidence.py verify \
  --manifest /absolute/evidence-package/manifest.json
python3 eng/issue735/analysis/package_evidence.py restore \
  --manifest /absolute/evidence-package/manifest.json \
  --output /absolute/restored-evidence
```

All parts and the manifest are required for restoration. The packer checks each
original file, keeps ZIP bytes unchanged, and only replaces an extracted copy
with a reference after a length/hash match and a full streamed byte comparison
against the original ZIP entry. Invalid ZIPs are retained and never used as
reconstruction sources. Large files can span parts; restoration checks the
reassembled hash. The package retains the original internal symlink target and
restores it as a relative link inside the new evidence root.

Temporary signed download URLs in JSON receipts are redacted in the delivery
copy; the input is unchanged and both hashes are recorded. Public GitHub artifact
URLs remain. A conflict inside an original ZIP is reported instead of rewriting
that ZIP. The packer finishes with an independent complete read-back verification;
source changes, missing bytes or reconstruction errors leave `INCOMPLETE` and
a nonzero exit. Inventory-only output is not proof of final ZIP or byte checks.

Synthetic pack/restore guard cases are separate from production and performance
evidence. Their retained failure history includes a mistaken fixture file count;
the correction changed the synthetic fixture expectation, not any product test,
performance sample or raw evidence.
