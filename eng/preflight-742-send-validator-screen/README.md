# Bounded D/G2/V send-validator screen

The required independent inspection of actual G2/V compiled paths from safety
run 37823636416 has passed. Its exact saved proof, ELF hashes, source identities
and check results are pinned in `codegen-review.json` and `codegen-proof.json`.
Both workflow preparation and the runner require that reviewed proof. Missing
symbols or collection success alone never satisfy the gate.

The workflow at `.github/workflows/742-send-validator-screen.yml` matches the
retained template. The stage runs only the already-approved bounded JIT screen.
Mechanism proof makes no performance claim; throughput and full acceptance
remain separate. There are no controller micro or NativeAOT timing launches.

## Exact source and inherited runner

The current helper base is `598ca7337bc6c832d0b1daa7d8058a53f8c7de5c`, which
adds only the two approved artifact-repack files over the explicit safety/source
helper `815c1ac1ec1ec4141f38cfe06814d8d0caf4b257`. Every phase-one file is
checked byte-for-byte against that safety helper and its hash is recorded.
Phase-one files and all older helpers remain unchanged. Preparation directly reuses the
inherited quiescent reconstruction recipe, then adds only the frozen production
patch. It does not invoke the phase-one preparation guard from a changed helper.
The execution head, current helper base, safety/source helper, reconstruction recipe and helper hashes are
recorded separately.

- D: dev commit `0fe26024b114bb6e78411a9b86276086c045d03d`
- G2 production: `398d484fb5b8ab8adb75a74db7d577d929d2dc77`
- V production: `379e7ca76cc3431d464e8b3ffc56e245534b151c`

V's `aaf1267b` unit overlay is never an input. D receives only the same two
original benchmark-harness files used by earlier screens. Every production
source and every JIT application/dependency file is hashed before and after the
population. Source identities and common measurement-harness bytes are also
checked before and after. Output must be a fresh directory.

`screen.py` is the existing quiescent screen with bounded changes: arm labels
and pins, the predeclared TCP control, updated population counts, and the
code-review/source gates. Workload command construction, environment, timeout,
raw-report validation and paired comparison calculations are retained. It
imports the original `common.py` directly, without modifying its validators.

## Fixed population

Each of the six permutations of D/G2/V uses these five workloads in this order:

1. TCP Client100x16
2. SharedMemory Client100x4096
3. SharedMemory Server1x16
4. SharedMemory c8 size 1, operation all
5. SharedMemory c8 size 10,000, operation all

Exactly 90 launches produce 198 validated rows: 54 RPC rows and 144 c8 rows.
Each c8 report retains unary/c2s/s2c/duplex in that exact original phase order.
Every arm appears in each position twice; no workload or operation is omitted.

RPC keeps 30 warmups, 5 seconds and the 200,000-operation cap. c8 keeps one
connection, concurrency 8, stream/connection windows 8,192/65,536 bytes,
one-second warmup, two-second measurement and recording off. Original JIT
settings remain ReadyToRun=0, TieredCompilation=1, TieredPGO=1 and
QuickJitForLoops=1, with processor count and affinity fixed to four available
CPUs on the same runner. There is one attempt per launch, no resampling,
replacement, failed-row exclusion or threshold change.

Raw logs/reports, command lines, exit codes, before/after identities, operation
counts, cap flags, measured windows, CPU, allocations, latencies and failures
are retained even when validation fails. Comparisons are V versus D and V
versus G2, preserving all six paired repeats. Collection success is diagnostic
evidence only. Original full acceptance and result review still remain.
