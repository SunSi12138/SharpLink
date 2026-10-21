# Same-session cold-control and physical-ownership acceptance ledger

Status: research only, 2026-10-21. No implementation in these experiments is a production quiescence API, and no partial cleanup candidate is recommended for publication as such.

**Access limitation:** the detailed prototype/source/IL/JIT/raw-results bundle
is sealed locally but was not authorized for public archive upload. This public
narrative does not supply a remote replay or claim that the branch CI runs those
prototypes. See the [public evidence index](../eng/research/cold-control/README.md).

## Result first

1. Strict cooperative, whole-server, same-contract no-overlap coordination remains architecturally plausible without a new per-RPC epoch or counter. Existing Ready checks, selection reservations, admission-program ownership and request permits provide useful cut points. It is **not** implemented or fully proved by these experiments.
2. Pending-call counts and module leases are not physical user-code reclamation. Reproductions show request codecs and interceptors still running after public deadlines and logical counters reach zero. Generated server streams have the analogous framework-abandoned MoveNext plus invalid concurrent Dispose problem.
3. A runtime-scoped exceptional-work registry can retain the examined physical tasks without a process-wide registry or new normal counters. That alone is not complete ownership: linked cancellation sources can already have been disposed, and static Stop lacks an entry-sealing boundary.
4. Owner-representation experiments meaningfully improve on simply adding hot fields. The best measured consumed-owner form reduces allocation by 48 bytes per intercepted short-circuit stream and avoids the demonstrated completed-object roots. It still changes ordinary deadline work and JIT shape; no zero-added-work or end-to-end production claim is established. These measurements do not prove an inherent impossibility of a better 3.0 representation.
5. Therefore the supported deliverable is an evidence/conclusion issue and separately verified tooling, not an incomplete cold-control runtime PR. Preserve the positive architectural boundary and the concrete remaining obligations rather than declaring all same-session approaches impossible.

## Scope and identities

- Source baseline: `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`.
- Baseline production source was unchanged; prototype builds used an isolated source archive.
- Comparison artifacts named below are local provenance, not files included in this public branch.
- SDK 10.0.102, Release net10.0, Linux x86-64. Native measurements disable tiering (`DOTNET_TieredCompilation=0`), FullOpts without dynamic PGO. Dumps use `DOTNET_JitDisasm` scoped to the measured types and `DOTNET_JitDisasmAssemblies=SharpLink.Client` (or `SharpLink.Runtime` for the pump). These pinned optimized structural observations are not default-tiered/PGO production performance claims.
- `pure-baseline/SharpLink.Client.dll` was copied from the baseline checkout's Release output; tracked client source is unchanged. Its intercepted-stream IL/layout/native code matches the earlier `partial` control, whose only Interceptors edit replaces a timeout observer with runtime tracking.
- Client DLL SHA-256 prefixes: pure baseline `c9eabfdac2b11da05`; partial `e463b64abaa348242`; state-owner `0d10bc215bb4ca74`; context-clock `a47b44a4ab131257`; exchange-owner `76ec074f27d5fc4f`; exchange-direct-owner `b5a70681d55823f2`; exchange-state-helper `09640180790138bb`. Full source/DLL/patch hashes are in `candidate-manifest.json`.
- Final source archive contains `exchange-state-helper`; every preceding source/DLL is preserved separately. `owner/` also preserves the earlier, rejected state-owner DLL identity and IL/native evidence with unnecessary token hoisting and +8 bytes per call; its exact historical source was not separately snapshotted, so the later `state-owner` source is not claimed identical to it.

## Candidate comparison

Allocation is measured for synchronous, locally short-circuited intercepted ServerStreaming with 0, 1 and 100 items; 2,000 warmups and 20,000 measured calls. All three item counts give the displayed bytes/call. This is not a network benchmark or production throughput certification. Timing varies across runs and is not used to assert speed equivalence.

| Form | Wrapper fields / iterator fields | Iterator MoveNext IL bytes | Bytes/call | Static completed-object roots | Disposition |
|---|---:|---:|---:|---|---|
| Pure baseline, and partial stream control | 7 / 17 | 1514 | 1712.218 | None of the three | Missing physical stream ownership |
| Existing full state owner, never consumed | 4 / 20 | 1701 | 1712.218 | Request, codec, replaced interceptor retained | Reject retention regression |
| Replace existing clock ref with runtime context | 7 / 17 | 1547 | 1712.218 | None of the three | Actual extra ordinary clock dereference; not zero work |
| Consume owner with existing once-only atomic, then snapshot inputs | 3 / 22 | 1765 | 1728.218 | None of the three | +16 B, extra snapshot stores; reject this form |
| Consume owner, use its fields directly | 3 / 18 | 1729 | 1664.218 | None of the three | Better allocation, but ordinary dereference/copy changes; not accepted as zero-work |
| Consume owner, reuse existing state-local deadline helper | 3 / 18 | 1633 | 1664.218 | None of the three | Same allocation improvement; main native code smaller, but ordinary owner/clock loads remain |

The weak-reference test keeps **both completed enumerable and completed enumerator** alive, and independently probes request payload, per-call codec, and replaced interceptor generation. A stream implementation that captures none of those is used. Baseline static references collect; state-owner static references survive; context-clock and all consumed-owner variants collect. The dynamic deferred wrapper retains all three in **every** form, including baseline, so that pre-existing dynamic root is not mislabeled a new regression.

Native evidence:

- Baseline iterator main MoveNext: 3896 bytes; setup await: 1615 bytes; GetAsyncEnumerator: 76 bytes; deadline helper: 239 bytes, 40-byte stack reservation.
- Context-clock deadline helper: 269 bytes, 88-byte stack reservation. The code actually loads context then clock and spills/copies the deadline on the logical-call branch. This is ordinary work, not merely larger cold code.
- Snapshot consumed-owner iterator main: 5223 bytes; direct consumed-owner main: 5208 bytes; both setup-await main: 2150 bytes. Total native size is **not** an executed-path instruction count.
- Consuming a null reference with Interlocked.Exchange lowers to inline `xchg`, not a GC helper. The direct form then hoists the owner into its iterator and reads state -> client -> runtime -> clock on each relevant deadline check. Its by-value helper also causes 40-byte deadline copies. Existing state-local deadline logic could avoid some copied-argument duplication; this remains a representation trade-off, not a demonstrated allocation lower bound or impossibility theorem.
- Final bounded comparison (`exchange-state-helper`, DLL SHA-256 `09640180790138bb78a16ad3da688e5aa6ef5ba8e29110a70cb442dee9219c92`) reuses the existing state-local ThrowIfFrozenDeadlineExpired rather than passing copied inputs. Iterator main native is **3866 bytes, smaller than baseline 3896**, while the called helper is 434 bytes versus baseline 239. At the successful per-item call site, baseline passes its wrapper already in r15; this form loads owner from iterator and performs a null probe. The helper traverses owner -> client -> runtime -> clock versus baseline direct clock, and its readonly-field handling still copies deadline state. The allocation improvement remains 48 B/call; all 41 cases pass. Smaller main code does not certify zero extra work, and larger helper code does not prove a throughput loss. This is useful intrinsic representation progress with unresolved acceptance, not an impossibility result.
- The compiler clears the direct consumed-owner iterator local on completion in the measured build, consistent with the held-enumerator WeakReference result. No hand-written per-success owner-clearing store was added.
- Generated server pump: 23 state fields in both; main IL 1620 -> 1795, EH regions 4 -> 5; entry stub remains 106 IL bytes; native main 5517 -> 5731 bytes (latest rerun; an earlier dump measured 5728). No new counter/field does not imply identical JIT behavior.

Artifacts per form contain exact `SharpLinkClient.Interceptors.cs`, DLL, IL and results. Native dumps are `pure-baseline/jit.txt`, `partial/jit.txt`, `context-clock/jit.txt`, `exchange-owner/jit.txt`, `exchange-direct-owner/jit.txt`, `exchange-state-helper/jit.txt`, and `server-{baseline,candidate}/jit.txt`. The completed-enumerator A/B reruns are `partial/held-enumerator.json`, `state-owner/held-enumerator.json`, `context-clock/held-enumerator.json`; the last two forms and pure baseline use `results.json`.

## Common research mechanism and limits

Changed files are enumerated in `candidate-manifest.json`; complete reconstruction patches are in `patches/`. They are expressly research-only. The common patch is the **latest test harness**, not a claim that every earlier partial candidate passes every latest regression.

- `SharpLinkRuntimeContext.cs`: only class -> partial.
- New `SharpLinkRuntimeContext.AbandonedWork.cs`: nested static CWT keyed by the runtime's unique existing `SharpLinkBufferWriterPool`; no added runtime instance field, no eager context mapping, no process-global task drain. APIs are `TrackAbandonedUserWork(Task)`, static `TrackAbandonedUserWork(SharpLinkBufferWriterPool, Task)`, and `CaptureAbandonedUserWorkDrain()`.
- Tracking publishes an observation before observing completion. Drain recaptures after each snapshot so nested abandonment registered before an enclosing retained wrapper completes is included. Callers must first close and drain ordinary owners. The registry itself is not an admission gate.
- `ClientAssemblyRegistry.cs:512,542-548`: retire module, await module leases, then exceptional work. Grace timeout can return `ReferencesReleased=false` even when RemainingCalls/Streams are zero. Eventual cleanup waits physical completion.
- This registry conservatively couples modules sharing one runtime: unrelated abandoned B work can delay A release. It does not couple separate runtimes, but it is not precise per-module ownership. Uncooperative physical work may retain indefinitely; a timeout does not free it.
- `SharpLinkClient.Interceptors.cs`: ClientInterceptorState already owns the client, so its timeout-only replacement can register the task without a new successful-call owner capture. Stream wrappers need one of the representations above.
- Client/server abandoned MoveNext cleanup is one physical task: force-yield without SynchronizationContext capture, await pending operation, then Dispose. Calling compiler-generated iterator Dispose concurrently with MoveNext can throw NotSupportedException and lose finally execution. A synchronously blocking Dispose must also not delay the public deadline.
- `RpcSession.GeneratedServerBridge.cs`: generated-server prototype reuses existing session runtime and existing deadline-won state. It catches cancellation of the waiter as well as deadline expiry and retains sequential cleanup. This is an ownership experiment, not evidence that all runtime ownership sources have been audited.

### Stop exclusion, measured

`SharpLinkClient.cs:210,243` adds a final registry drain before runtime disposal, but this only covers work already registered (or transitively registered by retained work). Static local interceptors can start before Stop, remain normal unregistered work while Stop captures an empty registry, then time out and register **after** runtime disposal. `_activeLogicalInvocations` is not a complete Stop closure and this candidate adds no pre-interceptor gate. Do not claim full physical Stop. Dynamic retirement is stronger because its existing permanent module gate encloses interceptor entry.

`ColdQuiescenceShutdownBoundaryTests.StaticInterceptorCanRegisterAbandonmentAfterStopDisposedRuntime` passes as an expected counterexample, not a successful cleanup test. Log: `/tmp/cold-owner-stop-boundary.log`.

### Linked cancellation-source exclusion, measured

`SharpLinkClient.DynamicChannel.cs:113-137,163,204,214-227` disposes the caller+module linked CTS when the public wrapper completes. Keeping only the inner interceptor task does not keep that linkage alive. After a deadline, Unregister with zero grace cancels the module: caller `None` reaches the task through its direct module token; a cancellable-but-live caller token does **not**, because the linked CTS has been disposed. A cooperative cancellation-waiting interceptor can then prevent reclamation forever unless independently released.

`ColdQuiescenceCancellationOwnershipTests` has both controls, passed 2/2; `/tmp/cold-owner-cancellation-tests.log`, `state-owner/cancellation-characterization.json`. Test-only independent release avoids leaving an immortal task. A possible failure-only transfer of the existing linked CTS remains unimplemented; it must preserve transitive work ownership and avoid a cleanup helper waiting on a registry that includes itself. No successful-path owner/counter has been added to disguise this gap.

### Inbound mailbox correction

The archive includes an experimental `PreAdmissionStreamDispatcher.Ownership.cs:83` registry call for incomplete child disposal. A custom internal asynchronous-dispatcher test is **not** evidence that supported built-in successful inbound calls need this tracking. The subsequent focused proof uses the actual pooled dispatcher: active child leases cover codec and terminal execution; generated server inbound streams have no asynchronous consumer-abandonment callback; properly awaited service/consumer work is covered by invocation ownership. Five controls passed 10 Debug and 10 Release repetitions (100/100). Under that explicit application-cooperation boundary, this mailbox addition is unnecessary experimental breadth and can be omitted.

Evidence: [built-in inbound report](builtin-inbound-disposal-research.md) and its locally retained artifacts. Arbitrary application-escaped concurrent Dispose/MoveNext remains out of framework-owned quiescence scope. This does not remove the real generated outbound framework-abandonment obligation.

## Test ledger

All test sources below are in the local experimental snapshot under `test/SharpLink.UnitTests/`.

- `Client/ColdQuiescenceOwnershipAuditTests.cs`: original 2/2 baseline counterexamples, static/dynamic blocked serialization after a delayed interceptor. Public deadline, pending/auxiliary/selection/logical counts drain while codec still executes; late Request publication is correctly rejected. `/tmp/cold-quiescence-audit-direct-test.log`.
- `Client/AbandonedInvocationOwnershipCandidateTests.cs`: 20 cases. Three blocked-codec shapes; five invocation shapes times late next/fault/cancel; already-abandoned static Stop; registry transitivity and runtime isolation. ClientStreaming has its existing independent producer lease and is not falsely asserted to have zero leases.
- `Client/AbandonedStreamOwnershipCounterexampleTests.cs`: six candidate regressions, two stream shapes times pending MoveNext deadline, the same deadline with slow subsequent disposal, MoveNext cancellation. The original six baseline counterexample assertions are preserved separately in `partial/AbandonedStreamOwnershipCounterexampleTests.cs`.
- `Client/AbandonedIteratorSequentialCleanupTests.cs`: four compiler-generated iterator deadline/cancel cases, verifies eventual finally and exactly-once sequential disposal while scopes remain alive.
- `Runtime/AbandonedGeneratedStreamOwnershipCandidateTests.cs`: six generated-server cases: deadline/cancel x late fault; synthetic mailbox disposal; synchronous Dispose race. The mailbox case has the limitation above.
- `Client/AbandonedOwnership{Representation,Retention}Measurements.cs`: five measurement/probe cases, not throughput acceptance tests.
- These Abandoned* groups total **41**, including five measurement cases. Direct consumed-owner and final state-helper Release builds: zero warnings/errors, 41/41, `/tmp/cold-direct-exchange-owner-build.log`, `/tmp/cold-direct-exchange-owner-tests.log`, and `exchange-direct-owner/regressions.json`. Final helper run is `/tmp/cold-direct-state-helper-{build,tests}.log` and `exchange-state-helper/regressions.json`. Do not call this 41 production tests or a full suite.
- Separate Stop and CTS characterizations intentionally pass by asserting the remaining bad behavior. No favorable regression run overrides those counterexamples.

The sealed local bundle has portable reconstruction scripts, exact patches,
source, hashes and raw test reports; its final script was executed successfully
from a fresh baseline archive. That bundle is not publicly available, so these
prototype counts are local observations, not results reproduced by this branch's
CI. The public same-session TCP tests are a separate unchanged-runtime fixture.

## Positive same-session architecture boundary and remaining proof

The following line references are to baseline source, relative to `src/`.

1. Freeze **all** participant sessions, pool growth/reconnect and Ready publication, plus all service/admission registry mutation. Server `_services` is shared, not an isolated per-domain table. `SharpLink.Server/SharpLinkServer.ConnectionLifecycle.cs:155-169` has distinct manifest-capable and non-capable publication paths; both require closure or rejection.
2. Pause through existing Ready equality. `SharpLink.Client/ClientConnection.cs:202-221` has the selection reservation double-check; `:335-347` covers untracked work. `SharpLink.Server/ServerConnectionState.cs:165-189` has the request-permit double-check. Do not introduce a new normal epoch/paused branch or atomic to simulate proof.
3. Drain selection reservations and pending/untracked publication, then enqueue a **normal FIFO Request-queue fence**, not priority Ping/ACK. `SharpLink.Runtime/RpcSession.cs:376-385` classifies progress traffic, and `RpcSession.SendPump.cs:94-100,201,231-243` permits progress interleaving. Priority acknowledgement is not a preceding-data ownership barrier.
4. The reader directly invokes DispatchRequestAsync before continuing: `SharpLink.Server/SharpLinkServer.RequestLoop.cs:271-280`. The synchronous prefix captures admission ownership before its first queue await. Enabled program uses are acquired by `SharpLink.Server/Admission/AdmissionProgram.cs:271-300`, then call state owns them through `SharpLinkServer.InvocationDispatch.cs:83-157` / `SharpLinkServer.AdmissionDispatch.cs:78-144`. Disabled paths obtain ServerRequestPermit before decode (`InvocationDispatch.cs:183,209`; OneWay `AdmissionDispatch.cs:158,218,372`). All captured program generations, not only the current one, must drain. Queued continuations can re-read service maps (`AdmissionDispatch.cs:536`).
5. `Admission/ServerCallAdmission.cs:60-99` PendingCallAdmissions closes an in-flight ownership transfer. A local active-count snapshot alone is insufficient. Do not reuse irreversible lifecycle-drain completion sources (`ServerLifecycleCoordinator.cs:567-595`) for pause/resume, nor place a Paused enum value where existing >=Draining comparisons treat it as terminal.
6. Reader fence handling records ownership asynchronously and returns. It must keep StreamData/Cancel progressing. A control RPC cannot hold the same admission/module lease it is waiting to drain.
7. Late cancelled client serialization cannot publish a Request because pending identity is checked under CompletionGate (`SharpLink.Client/PendingRequestTable.Publication.cs:44-111`; `SharpLink.Client/SharpLinkClient.InvokerSend.cs:65,77,94`). That publication safety is not codec reclamation, as the tests show.
8. Dynamic client gates can permanently retire old generated proxies ahead of interceptor/codec execution. Deferred streaming acquires on first enumeration. Do not reopen a retired gate: otherwise Ready-Paused-Ready allows stale pre-admission calls to rejoin. Current-at-admission is defensible for immutable compatible static contracts, but not silently equivalent to exact old registration or incompatible-codec safety.
9. Whole-server same-contract cut can leave immutable client codecs/static contracts outside reclaim scope. Coordinated dynamic client replacement additionally needs all framework-abandoned task and CTS ownership above. Prepare/seal/commit is required: current Replace methods expect Running old owners and cannot simply be invoked after pre-retirement. Failure after retirement must not reopen an old owner; rollback needs a valid new owner/transaction state.
10. Same physical session must survive Pause/Fence/Resume, preserving authentication continuity. Participant failure, cancellation, concurrent replacement, admission queues, OneWay, streams, stalled codecs, stale proxies, non-capable peers, resumed new publication and commit failure need deterministic end-to-end tests. Uncooperative work may block indefinitely; timeout must report incomplete, not reclaim.
11. Identical old/noncooperative request bytes after Resume are indistinguishable without identity on the wire. A cooperative 3.0 lifetime contract may deliberately replace #775's stronger interpretation, but must say so. This is not security isolation or arbitrary incompatible-codec wire proof.

No complete FIFO-fence/pool-freeze/transaction implementation, hot-path certificate, complete unload proof, or full775 guarantee is included here. The counterexamples are specific missing ownership and cost evidence, not a reason to silently give up the viable compatible-server scope or claim that future intrinsic redesign is impossible.

## Local sealed replay bundle

The local source-only bundle is `physical-ownership-research-evidence.tar.gz`,
SHA-256 `66adf52799e552f7c5c3235bee4a66a6cb05dc8502bb59e54430b0e58dd6ac5b`.
It contains no compiled binaries. Archive publication was not authorized. The bundle was not extracted
into this branch or repackaged to bypass that decision. The public branch therefore
cannot independently replay these ownership experiments. This access limitation
is separate from the technical acceptance limitations above.
