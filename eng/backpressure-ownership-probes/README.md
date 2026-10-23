# Intrinsic backpressure ownership reproduction probes

These optional, source-only probes invoke the **real built runtime** from two existing
checkouts. They are outside the solution, add no unit cases, change no production
visibility, and do not alter canonical CI. The friend assembly name is the repository's
existing `SharpLink.Benchmarks`. No previous binary, raw archive, or generated log is
checked in; every invocation generates fresh evidence locally.

## Run

Install the official SDK selected by the repository `global.json` (10.0.102 for the
original comparison), plus Python 3. Use two existing checkouts and a **new** output
directory. Checkouts must have no tracked changes or untracked source/build inputs;
commit local probe edits before running. The runner never fetches, checks out, pushes, uploads or deletes anything.
It refuses an existing output directory rather than overwriting negative samples.

```sh
python3 eng/backpressure-ownership-probes/run.py /path/to/baseline /path/to/candidate \
    /path/to/new-evidence --smoke
# Full native/allocation matrix; omit --smoke. Add --no-restore only if runtime
# projects have already been restored. Probe projects restore normally.
python3 eng/backpressure-ownership-probes/run.py /path/to/baseline /path/to/candidate \
    /path/to/new-full-evidence
```

The SDK must come from the official .NET distribution; this runner does not install it.

The original baseline is `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`; the intrinsic
replacement source is `276d84fa4fe840084f647cb4bc6f5e284c613c9e`. A stacked branch can
contain unrelated earlier changes: record that provenance and do not attribute every
binary difference to this patch. The runner verifies the same selected SDK, captures
checkout commit/status, production source hashes, runtime binary hashes, emitted IL,
actual native headers, layouts and every sample. Missing requested front-door native
tiers fail the comparison; smoke explicitly requests FullOpts only. It checks source cleanliness and commit identity again at completion, and captures
harness hashes before building and verifies them unchanged afterward. It uses isolated
probe build outputs and serial builds. Both runtime checkouts receive ordinary ignored build outputs.

The full run takes several minutes and includes four **observed** JIT modes, six
blocked/cancellation configurations with 100 warmup + 500 measured batches, and both
cost witnesses below. A baseline accepted-16 run can strand; its failed status, log
and partial samples remain failures, never a shortened successful throughput series.
`status.json` is authoritative about completion; read it even when the runner exits 0.
A smoke run validates execution only, not the full performance claims.

Currently characterized on Linux x64, .NET10.0.2. Object sizes, emitted instructions,
tiering and timing are platform/runtime dependent. Use actual output headers rather
than assuming environment variables caused promotion. Run quiet paired campaigns for
throughput conclusions; this source package does not establish a timing threshold.

## Probe boundaries and the two rejected-cost witnesses

- `structure`: token-resolved IL and raw IL hashes, actual allocated object sizes,
  emitted field-address offsets and `Unsafe.SizeOf` for value types.
- `fast`: real pooled normal Response construction, immediate successful send and
  asynchronous pump release. Records producer **and process** allocations for all
  eight batches. Nonzero samples are retained. Frame construction/idle waiting and
  runtime activity are included, not attributed blindly to a new per-RPC allocation.
- `saturated`: holds a real flush, preconstructs frames, then issues 1 or 16 actually
  blocked sends. Token-present and actual-cancel-while-blocked are distinct cases.
- `witness`: the pristine `[1,3,2]` FIFO overtaking schedule and zero-queued-bytes plus
  completed flush while a normal pre-reserve send remains pending. Even the repaired
  runtime still needs the cold census before the fence; an ordinary fence alone is
  deliberately not upgraded into a universal producer seal.
- `late-recovery`: hold the actual admission gate, wait until a dedicated producer is
  blocked there after observing Full, release the original flush and observe idle,
  then permit the final reservation recheck. A rejected implementation allocated a
  temporary 32 B queue here. Baseline and final source measured 152 B/attempt, versus 184 B
  in that intermediate, including common fresh-thread/contended-lock setup. Compare
  deltas; do not relabel baseline 152 B as zero.
- `held-cleanup`: cancel a Full send whose custom writer Return remains held, release
  the original flush, then send 10 ordinary frames and one while the actual admission
  gate is held. Baseline/final keep the wake slot null and reach idle under that gate.
  A rejected implementation kept a retiring node in the wake slot, forcing ordinary
  release to lock/scan. The 100 ms scheduling allowance is a diagnostic witness, not
  a throughput sample or proof from absence of statistical significance.

Both cost witnesses work with a supplied older/rejected checkout as well as the
baseline/final source. The repository unit tests additionally enforce final-source
invariants with bounded synchronization, cancellation/grant/Stop races and held cleanup.
The probes do not inject production hooks or synthesize a copy of the send algorithm.

## Original measured results and limits

The final replacement keeps pump/session/frame sizes 144/312/40 B and the original
reference offsets. Its queue costs 48 B per blocked epoch, including the original pump
task and waiting/retiring boundary; its immutable marker costs 48 B once per type.
Actually blocked single-send producer allocation was 560→432 B without a token and
808→576 B with a token. Final 16-waiter batches measured387/471 B per send. True cancelled
process allocation was 3816→1912 B for one waiter and 3654.5→1807 B per send for 16 waiters.
These totals include the extra 16 B queue size over the rejected intermediate.

Token-resolved fast-entry IL and normalized non-PGO front-door instruction listings
matched. Native comparison masks address-shaped long hex operands and generated
state-machine numbers; raw listings remain available because that heuristic cannot
prove every masked immediate is an address.
Ordinary release keeps its previous wake-slot null guard; the nonempty routine is
outlined. Default PGO bodies differed, and a repeat of the pristine baseline also
changed native sizes. Neither profile variation nor nonsignificance proves zero cost.
Immediate allocations included nonzero process samples: final default producer 0 B over
4 million operations but process 12,304 B; FullOpts producer 64/process 3,096 B over 80,000.
Do not drop those samples or claim universal zero allocation or throughput improvement.

The local runtime commit added 26 tests: 22 ownership and 4 real server rejection cases.
Regular unit total 2149 was observed; an injected baseline of 2131 would become 2157,
which is arithmetic until that specific hosted run reports its result. Independent
focused rerun 96/96 passed. Broad local 2149 had 28 Unix-socket/IPC sandbox failures;
hosted broad CI is still required, not replaced by these probes.

Finally, the cold census joins **already registered intrinsic nodes**. It does not seal
future sends, paused pre-publication producers or all possible synchronous failed-return
work. Ingress and every ordinary/control/terminal producer handoff must first be
closed and physically joined, followed by the normal-FIFO fence. Session disposal's
join must not be described as a universal join of every producer that observed Full.
See `doc/adr/0002-intrinsic-send-capacity-ownership.md` for the ownership design.
