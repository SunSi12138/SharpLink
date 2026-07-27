# 0.8.43 regression-test research

## Audit boundary

- Exact source baseline: local 0.8.42 commit `cd2de157e05fd4b3d97dd34f056871c2c9d95ee8`.
- The audit covered shared-memory lifetime, flow-control hot paths, pending-call completion,
  streaming telemetry lifetime, and dynamic endpoint/admission-policy retirement.
- Every counted finding has an observed or deterministic pre-fix failure plus a post-fix control.
- A rare `WindowUpdate references an unknown stream` Integration failure remains an observation,
  not a finding: dispatcher disposal already closes admission and drains acquired dispatches before
  reporting consumer abandonment, and two complete reruns passed.

## Finding 1 — fresh shared-memory mappings can be unlinked during creation (P1 availability)

- `SharedMemoryMapping.CreateServer` ran stale-file cleanup before every mapping creation without
  coordinating concurrent creators. On Unix, one creator could unlink another creator's newly
  opened file before permission/header initialization or before its Client opened it.
- The first 0.8.42 final Unit gate failed in `File.SetUnixFileMode` after this unlink. A 64-round,
  eight-creator stress witness also failed from `CreateServer`; the deterministic companion
  `CreatingMappingShouldPreserveFreshPeerFiles` proved that baseline cleanup deleted a fresh peer.
- Pre-fix evidence: `artifacts/0.8.43-prefx-shm-two-witnesses.log` (494 pass, one expected fail).
- Fix: serialize in-process cleanup plus creation and exclude mappings younger than one minute.
  The five-minute stale-file control prevents deleting cleanup outright.
- Post-fix evidence: `artifacts/0.8.43-postfix-shm-cleanup.log` (495/495 pass).

## Finding 2 — ordinary streaming items take a redundant second flow-control lock (P2 performance)

- The isolated 0.7.11/0.8.41 comparison located the first regression at 0.8.0 commit
  `7a99fc66`: each consumed item first updated credit under `_gate`, then unconditionally entered
  the same gate again to inspect an almost-always-null cross-stream credit queue. Duplex streaming
  performs roughly 512 redundant lock acquisitions per RPC.
- Stock-versus-causal-removal paired throughput changed 4,832→5,155 (+6.7%), 4,795→5,255
  (+9.6%), and 4,810→4,986 (+3.7%); paired median +6.7%, P50/P99 -6.2%, CPU/stream -8.8%.
  Raw evidence and methodology are retained in the isolated performance checkout under
  `artifacts/bisect/raw` and `artifacts/causal`.
- MemoryDiagnoser corrected an earlier normalization artifact: size-32 changed only 6.57→6.58 KB
  and size-256 31.09→31.29 KB, so no product allocation regression is claimed.
- Fix: publish absence with a nullable queue, bypass `_gate` when null, and return the queue to null
  after its last item. Existing cross-stream threshold tests guard against stranded credits.

## Finding 3 — implicit connection closure is rewritten as Internal (P2 correctness)

- `PendingRequestTable.CreateCompletionException` had no `ConnectionClosed` case. Disposing the
  table could therefore complete a pending operation with reason `ConnectionClosed` but no explicit
  exception, after which the fallback constructed an `Internal` error.
- The deterministic completion test and the existing 512-way dispose/register race both failed on
  baseline; evidence is in `artifacts/0.8.43-prefx-pending-connection-code.log` (494 pass, two fail).
- Fix: synthesize a `SharpLinkException` retaining `SharpLinkErrorCode.ConnectionClosed`.
- Post-fix controls: all 21 pending-table tests and the complete 496-test Unit gate pass.

## Finding 4 — abandoning a client response stream reports successful telemetry (P2 observability)

- `ObserveStream` unconditionally completed its client call scope as successful in `finally`, even
  when a consumer disposed the enumerator before observing a terminal item or failure.
- `EarlyClientStreamDisposalShouldNotReportSuccessfulCompletion` consumes one server-stream item,
  disposes early, and inspects the stopped Client activity. Baseline reported `Unset`/success;
  evidence is `artifacts/0.8.43-prefx-client-stream-telemetry.log`.
- Fix: track whether the remote terminal was observed; otherwise complete with
  `OperationCanceledException` and increment the client `consumer_abandoned` counter.
- The exact targeted Integration test passes after the fix and verifies Error plus `error.type`.

## Finding 5 — stale topology selection recreates retired admission state (P2 resource lifetime)

- `DynamicClusterRuntime.GetReadyConnection` reads a published selection snapshot without holding
  the topology gate, as intended. A custom selector can still be running when an update retires and
  releases that endpoint generation. When selection resumes, `TryAcquire` lazily recreates breaker
  state before connection selection proves the old generation is no longer ready. No later topology
  event retires that recreated state, so repeated address churn can retain one sample-ring pair per
  stale generation.
- The deterministic test pauses selection on the old snapshot, publishes an empty topology, waits
  for lifecycle retirement, then resumes. Baseline leaves one active generation; evidence is
  `artifacts/0.8.43-prefx-stale-admission-retirement.log`.
- Fix: after a granted selection finds no connection, check the endpoint's release flag under the
  topology gate and repeat the idempotent lifecycle retirement outside the gate. If release has not
  happened yet, the normal release path remains responsible, covering both race orders.
- Post-fix evidence: `artifacts/0.8.43-postfix-stale-admission-retirement.log` (1/1 pass).
