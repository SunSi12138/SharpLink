# Issue 739: bounded SHM initiation scopes

Diagnostic validation only. Exact production source is dev
`eb99fe887cf2129d9b88441245ca0a4a6406b6c2`; the source checkout is never modified.
Frozen multiplicity projection/validation helpers are imported and hash-checked.

## Run

Hosted performance evidence requires SDK 10.0.112 / runtime 10.0.12:

```sh
python3 eng/validation/issue739/shm_scopes/run.py \
  --dotnet dotnet --output /fresh/output --work-root /fresh/projections \
  --budget-seconds 1380
```

Only SHM plain Add c1/c32, N=131072, warmup=32768. Each cell runs three
ABBA blocks then AA (28 fresh RPC processes total), plus separate atomic-counter
and empty-scope controls. No outlier removal or automatic follow-on experiment.
A remains vanilla production source and primary performance evidence.
B adds the prior owner counters plus two inclusive synchronous initiation scopes.
Both A and B execute the same 132-task/barrier prewarm topology. B warms its
ThreadStatic and counter paths. A first-observed scope ThreadStatic context
during a sample leaves its raw calltrees descriptive and scope-only/unpriced;
additive reconciliation is rejected because first-use setup can affect gross
bytes outside the scope. This is not proof of a newly created OS thread.

Local correctness only, with no SHM access or performance transfer:

```sh
python3 -m unittest discover -s eng/validation/issue739/shm_scopes -v
python3 eng/validation/issue739/shm_scopes/run.py \
  --dotnet /tmp/dotnet754/dotnet --output /fresh/local-output \
  --work-root /fresh/local-projections --correctness-only --controls-only \
  --operations 128 --warmup 64
```

A generated binary also accepts `scope-metadata OUTPUT` for read-only local A/B
async-layout comparison. `--tcp-only` is restricted to correctness mode and is
not part of the hosted protocol. Local SHM was denied and must not be retried.

## Scope definition

1. Existing client/server `reader.ReadAsync(ct)` expressions call a synchronous
   helper which gates on the actual `SharedMemoryPipeReader` type. Four exact
   callsites include client/server handshake and established protocol readers.
   The original SHM `ReadAsync` async body remains byte-identical. The helper
   returns its original `ValueTask<ReadResult>` unchanged. Inclusive bytes may
   include nested control waits; these are never separately priced.
2. The control-channel `RunReaderAsync` expression `_stream.ReadAsync(signal)`
   calls a synchronous helper returning the original `ValueTask<int>` unchanged.
   Its original Memory-based virtual read overload and default token are retained.

Before/after `GC.GetAllocatedBytesForCurrentThread` and timestamps surround only
synchronous invocation. Return-status queries and atomic result writes are after
the allocation/timing endpoint. Completed-success, pending, faulted, canceled,
and synchronous throw buckets have separate counts and inclusive byte totals.
Pending-at-return is not proof of suspension or `OnCompleted` registration.
Existing actual source registration counters remain separate.

Counters are static preallocated arrays and Interlocked operations. Shared
ThreadStatic depth detects either kind of nested scope, including same-site
reentrancy. Thread identity is checked for every scope. Quiescent epoch-checked
snapshots require no in-flight scope and no intervening counter mutation. They
bracket a wider interval than gross byte timestamps; raw first/last timestamps
are cumulative observations, not additive counters. First-begin includes empty
scope warmup. Overlaps, thread mismatch, invalid observations or counter
inconsistency reject the sample. Cold ThreadStatic observations are retained
as descriptive scope-only/unpriced rows; they never receive an additive residual.

An additional shared-depth detector flags priced owner markers inside scopes:
logical-helper entry, accepted operation registration, successful plain permit,
null-to-snapshot push, and snapshot-to-null restore. This conservatively detects
possible overlap between inclusive scope bytes and the conditional owner budget.
Any flag rejects additive reconciliation; raw scope totals are retained. This is
not a general-purpose allocation profiler or exact object attribution.

## Reconciliation and evidence

The diagnostic B ledger adds observed nonnested scope bytes to the previously
calibrated conditional `136*registrations + 64*plain permits + 72*context cycles`
only when all assumptions and overlap checks pass. N+1 payload/sentinel decisions and owner branch/transition balances are checked
by the conditional ledger; accepted registrations may legitimately be fewer than
N+1 because completion races can bypass helper entry or actual registration.
The price uses the observed registration count. Operation/snapshot pool misses
and capacity activity are preserved as outcomes, not rejection gates; changed
calibration assumptions leave the additive estimate unpriced.
The 72-byte term still assumes no unrelated AsyncLocal entries. No per-box price
or predicted number of SHM allocations is supplied. Residual is signed and never
clamped. Allocations after an original invocation returns, other paths and
measurement-boundary differences remain in the residual. Exact object counts
are unknown. Local 10.0.2 correctness runs never receive an owner price.

Raw B-minus-A ABBA differences and separate AA variation are retained for bytes,
CPU, QPS and latency. Never subtract observer overhead from vanilla A. Empty
controls require zero measured managed bytes; their CPU cost is descriptive.
Deterministic fake PipeReader/Stream fixtures verify original ValueTask identity,
all status categories, synchronous exception identity, absence of source
consumption/registration, non-SHM bypass, scope nesting and owner-overlap detection.

Every result retains runtime/source identities, process ID/start, full raw samples,
source patches, executed DLLs, input/projected source hashes, runtime CoreLib,
SDK compiler and host hashes, and end-of-run integrity rechecks. Reflected async
field layouts and builder metadata must match A/B for the client/server methods,
SHM reader and control channel, in addition to the prior owner metadata. No
production fix, merge, release, or expanded matrix is part of this validation.


## Prospective c32-only recovery

Run 38048366564 remains failed/partial and immutable. Its c1 cell completed all
three ABBA blocks plus AA. Its c32 cell stopped after sample 05-B because nine
first-observed ThreadStatic contexts tripped the original guard. The host's
final integrity rehash was not reached; offline artifact-hash checks do not
retroactively establish that missing end check.

After independent review, a new c32-only cohort is explicitly bounded to 14
fresh RPC processes (three ABBA blocks plus AA), plus the same two controls:

```sh
python3 eng/validation/issue739/shm_scopes/run.py \
  --dotnet dotnet --output /fresh/c32-output --work-root /fresh/c32-projections \
  --concurrency 32 --budget-seconds 1380
```

This is a prospective cohort with its plan recorded before execution. It does
not rerun c1, resume the old process sequence, pool old partial c32 rows with
new rows, or discard cold rows. C# instrumentation, projected source generation,
and frozen dependencies remain byte-identical to the reviewed original run.
Only runner/reporting policy, tests and this README change.

`WarmCurrentThread` and all `Begin` setup occur before the allocation endpoint.
Thus explicit cold setup is excluded from the inclusive calltree byte delta but
can occur inside process gross. Cold rows preserve counts, statuses, same-thread
checks, raw scope bytes, raw gross and observer metrics. Their additive sum and
signed residual remain null. Scope nesting, priced-owner overlap, thread
mismatch and invalid observations remain fail-closed. No inference about exact
object counts, actual suspension or thread birth follows from these observations.
