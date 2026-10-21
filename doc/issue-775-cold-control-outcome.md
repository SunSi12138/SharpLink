# #775 / 3.0 cold-control research: acceptance ledger

Baseline: `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb` (`dev` at the start of
this investigation). SDK 10.0.102, runtime 10.0.2 for local comparisons.

This investigation does **not** resume the stopped Binding/dual-ID optimization.
The hard requirement is that catalog/update coordination must not add work to
ordinary successful RPC execution. A different 3.0 API or generated ABI is allowed
only if the replacement is genuinely better, not if additional checks, copies,
ownership counters or retained roots are concealed behind a new abstraction.

## Executive finding

A cooperative, stop-the-world update of a fixed session cohort is a coherent
architecture. A same-physical-session FIFO delimiter is also useful. Neither fact
establishes a production physical-quiescence guarantee in the present runtime.

The tested existing-ownership barrier is **incorrect**: all its counters can be
zero while framework-started old iterator work is still executing. Correcting
that requires ownership of physical completion, including disposal and linked
cancellation lifetime, rather than simply moving a publication store or adding a
cold API around existing drain counters.

The ownership prerequisites were implemented as isolated experiments and compared
against the baseline. They are not deployed by this evidence branch. The final
representation comparison and independent review are summarized in [the ownership report](cold-control-ownership-representation.md); no
focused passing test or noisy timing sample is a zero-hot-cost certificate.

## Claims deliberately kept separate

1. **Compatible server implementation replacement.** Ordinary held proxies may
   reach the current compatible implementation. This is already documented in
   [public RPC semantics](public-rpc-semantics.md), not a new fixed-registration
   architecture. Publication and eventual reference release are already distinct
   in the existing replacement result.
2. **Local dynamic-generation retirement.** An exact old module gate can close
   permanently using its existing lease acquisition. This does not by itself
   fence all physical work, other clients, pooled sessions, or remote execution.
3. **Cooperative cohort maintenance.** All affected framework participants pause,
   physically drain both directions, commit the same prepared plan, and resume
   only the new ownership. This is a stronger distributed contract with explicit
   global pause and failure/recovery costs.
4. **Exact registration against arbitrary peers.** Identical requests on the same
   session with unchanged ContractId/MethodId cannot reveal which registration an
   arbitrary sender intended. Cooperative local old-proxy retirement is not a
   cryptographic or wire-level exact-registration receipt.

These observations do not prove that every zero-extra-hot-work architecture is
impossible. Identity might reside in an immutable session/domain or an already
required lifetime owner. Such a design still needs its own evidence.

## Acceptance ledger

| Route | Useful result | Blocking evidence / disposition |
| --- | --- | --- |
| Close old module before publishing a compatible replacement | Existing gate can prevent new old-module service leases | Not a new ordinary-proxy contract; queued registration/token mismatch reproduced; fallible publication work must be prepared before irreversible closure. Not accepted as the requested architecture. |
| Existing counters + same-session FIFO delimiter | Correct parser ordering, reservations, retired admission generations and normal stream cleanup demonstrated | Real TCP false-zero counterexample after generated stream deadline. Rejected as a physical fence. |
| Runtime-scoped exceptional work registry | Can retain abandoned tasks without a per-RPC counter; existing unique buffer pool can identify runtime ownership | Task ownership alone omits linked CTS lifetime and late static registration; every release boundary must join the physical owner. Not a complete fence. |
| Reuse full existing interceptor state as stream owner | No new owner allocation; optimized experiment matches aggregate allocation | Held completed static enumerable newly retains request, codec and replaced interceptor. Rejected as-is even though allocation matches. |
| Replace captured clock with runtime context | Restores baseline request/codec/interceptor collectibility and equal aggregate allocation | Native deadline-check path adds a context-to-clock load and duplicated deadline copy. Rejected for the no-extra-hot-work claim. |
| Consume owner through the existing enumeration guard | Direct-owner form is 48 B/call below baseline and restores tested completed-enumerable/enumerator root behavior | Repeated deadline checks change owner/clock indirections and copies; linked CTS and late-entry closure remain unresolved. Promising intrinsic representation, not an accepted physical fence or evidence that every representation must add allocation. |
| Framework-enforced cohort protocol | Bounded ordering models are safe under explicit physical-fence assumptions | Runtime physical oracle, topology freeze, prepared commit and authenticated operation/receipt protocol are not implemented by local handles. No production claim. |

## 1. Same-wire queued replacement is not generation-coherent

The test-only [counterexample patch](../eng/research/cold-control/queued-replacement-cancellation.patch)
holds an admission permit, queues a real dynamic RPC under R1, replaces the service
with R2 while retaining the **same contract Assembly object**, then lets the queue
resume. Only R2's handler starts. Forced retirement cancels R2's module token but
does not cancel the handler's captured token, which still belongs to R1.

The no-replacement positive control passes. The replacement case fails its exact
token assertion, not a timeout. Its fixture uses a separate collectible service
ALC but does not change the wire contract or silently substitute a new connection.

Mechanism: R1 descriptor and stream decisions are made before the wait, and
the wait state captures R1 cancellation. The continuation re-enters dispatch and
looks up the current service table. R2
can therefore be leased with an R1 cancellation state. A mere old-gate check before
the new lookup would race again. Correctness needs a coherent selected registration,
descriptor, stream shape and cancellation lifetime, or explicit rejection of a
retired selection.

Run in an isolated checkout:

```sh
bash eng/research/cold-control/run-queue-repro.sh
```

The runner requires the positive control to pass and the negative case to fail
at the exact expected assertion. Its green research job means **the production
bug was reproduced**, not that the bug has been fixed.

## 2. Real TCP disproves the existing-ownership physical fence

See [the full fence report](cold-session-fence-research.md). Thirteen cases pass
in Debug and Release, repeated ten times per configuration: **260/260 case
executions**. Twenty existing focused admission/OneWay/interceptor regressions
also pass. The combined checkout independently reran all thirteen Release cases.

Positive controls establish why selection reservations, untracked OneWay owners,
all retired admission generations, pending admission transfers, final server
request permits and normal stream disposal matter. The delimiter uses the same
normal FIFO queue and the exact same physical session, not priority Ping, pool
selection, a new connection, or a request-ID cutoff.

The negative deadline case then observes all of those owners drained while the
framework's generated-stream MoveNext and async Dispose remain gated. A second
case wraps a normal compiler-generated iterator: concurrent Dispose throws
NotSupportedException, the abandoned MoveNext later returns true, and framework
disposal is never retried. The iterator's finally starts only when test cleanup
explicitly invokes a second Dispose.

Initial wall-clock tests sometimes selected client cancellation instead of the
server deadline. Those failed samples are retained. The final fixture isolates
the server deadline using a frozen client monotonic clock and explicit gates; it
does not weaken production timeouts or infer correctness from a sleep.

Three additional direct runtime characterizations distinguish normal pump
completion (which awaits disposal), deadline completion (which does not), and a
**custom internal held-disposer** mailbox case. The latter is not evidence that
built-in cooperative inbound cleanup lacks physical ownership.

The [built-in inbound follow-up](builtin-inbound-disposal-research.md) ran five
controls ten times in both configurations: **100/100**. Those controls use an
isolated **ownership-candidate snapshot**, not pristine dev, to verify that no
exceptional registry is ever created for correctly awaited built-in inbound
cleanup. Existing child leases cover codec/terminal dispatch and invocation
ownership covers awaited consumer work. Thus that experimental mailbox tracking
branch can be omitted for this scope; it is not an unavoidable successful-path
cost. This does not remove the generated **outbound** abandonment counterexample.

This concerns **framework-started** physical operations. Arbitrary application
tasks spawned and detached by a service remain application-owned; a framework
cannot certify that every such background activity has stopped without an
explicit cooperation contract.

## 3. Physical ownership is larger than task observation

Client-side counterexamples also hold a request codec or local interceptor past
its public deadline while logical/pending/selection owners report zero. Existing
pending identity checks correctly reject late Request publication, but that
publication guarantee does not mean the old codec has physically stopped.

The isolated prerequisite experiments use a runtime-scoped registry keyed by the
already unique buffer pool. Registration occurs on actual abandonment; ordinary
successful calls do not increment a new global work counter. A recapturing drain
includes work registered by an already captured task before that task finishes.
It is not an admission gate and is safe only after relevant parent owners stop
creating new work.

The experiments exposed and tested further obligations:

- Await pending MoveNext before disposing its enumerator. Concurrent disposal is
  invalid for ordinary compiler-generated async iterators.
- Force asynchronous cleanup entry before invoking potentially synchronous user
  Dispose, so a just-completed MoveNext cannot block the public deadline before
  its physical-owner task is registered. Do not capture an arbitrary ambient
  SynchronizationContext for this transfer.
- Retain linked cancellation sources with physical work. Disposing a caller/module
  linked CTS at the logical deadline disconnects later module forced cancellation.
  A direct-module-token positive control and linked-token counterexample differ.
- Static interceptor entry is not sealed by current Stop. A later timeout can
  register abandoned work after an empty registry was captured. Dynamic module
  admission gives a stronger parent lifetime, but must not be generalized to Stop.
- Runtime-wide abandoned work may conservatively delay unrelated modules in that
  same runtime. There is no process-wide work set or cross-runtime/process-wide wait.
- Equal object allocation does not imply equal roots or native work. The full-state
  representation retains request, codec and replaced-interceptor objects in a
  previously clean static completed-enumerable case (this fixture is not an
  executed collectible-ALC unload proof); the narrower context representation adds native work.

The best measured intrinsic stream representation consumes the existing owner
through the existing once-only enumeration guard and reuses its existing deadline
helper. Its allocation is **1664.218
versus 1712.218 bytes/call** in the synchronous intercepted short-circuit fixture
(0/1/100 items), and request/codec/replaced-interceptor objects collect with both
completed enumerable and enumerator held. The superseded snapshot form instead
adds 16 bytes; that is not an allocation lower bound. The final iterator native body is **3866 versus 3896 bytes**, smaller than
baseline, so native-size rejection would be wrong. Native size alone is not an
executed instruction count. The final form still loads/probes the iterator owner
and follows state/client/runtime/clock references; measured readonly deadline
copies remain. These are actual changed ordinary work, while
overall throughput equivalence and complete physical ownership are not proved.
The 41 passing focused candidate cases include five measurement probes, not a
full runtime-suite or production-quiescence certification. This is a measured
representation improvement worth separate evaluation: extra individual loads do
**not** prove worse total call performance. It does not establish the strict
control-specific no-added-participation claim either. The demonstrated linked-CTS
hole blocks even dynamic-only retirement. The late static-entry hole additionally
blocks any full/static Stop claim, not a deliberately dynamic-only contract.
These ownership defects, in their respective scopes, block production acceptance
regardless of the allocation/native-size improvement.

A helper that awaits a registry's recapturing drain must not be registered into
that same registry, or it can wait for itself. An exception-only linked-CTS
transfer through a cold module-to-runtime owner is a concrete remaining option,
not a reason to leak CTS objects or add an unmeasured normal-path lookup.

## 4. The protocol is conditionally coherent, not implemented

[Model and API report](../eng/research/cold-control/model/README.md) and executable
standard-library Python checkers are included. Four coordinator scenarios exhaust
7,725 / 425 / 3,033 / 463 states. The bidirectional FIFO model exhausts 1,350 states.
Every correct scenario reaches successful terminal states; all fourteen weakened
variants produce concrete counterexamples.

The models require a real physical oracle. The modeled safe ordering is:

1. Prepare the complete registration/codec dependency closure and obtain exclusive
   cold writer ownership, rejecting stale preparation before irreversible closure.
2. Freeze the **whole affected session cohort**, including all clients able to
   reach the server-global registration, pool growth, reconnect publication and
   admission-policy generations. New sessions remain quarantined.
3. Permanently close exact old dynamic admission, drain client producers, send a
   normal-FIFO fence, drain server dispatch/queues/streams/sends/disposal, send its
   ordered acknowledgement, then drain client response decoding/consumers.
4. Commit one operation/digest across the fixed participant incarnations. Resume
   only after all required commit acknowledgements, never reopen retired gates.
5. Caller wait cancellation stops observation only, not the operation or permanent
   retirement. Treat protocol timeout/disconnect as incomplete work or a closed
   recovery state, not reclamation or rollback. A disconnect cannot retract an already
   transmitted Resume, so promise generation coherence, not simultaneous uptime.

Operation, digest, incarnation and management authorization are cold protocol
requirements. An ordinary authenticated RPC client must not gain server-wide
update authority merely because a new control frame exists. Long/uncooperative
streams can make maintenance wait indefinitely. This pause cost is real and must
not be hidden as a transparent per-contract hot swap.

## 5. Validation and the independent tooling correction

The local restricted environment completed Release build with zero warnings/errors
but the broad baseline unit suite had **2,095/2,123 pass, 28 fail**, and integration
had **417/467 pass, 50 fail**. Logs preserve direct SocketException(13) failures,
expected-exception mismatches caused by earlier socket failures, and a distinct
shared-memory bad-handshake accept timeout. They are not relabelled passes.

An unrestricted pinned-baseline [CI run](https://github.com/SunSi12138/SharpLink/actions/runs/39636374194)
passed 2,123 unit, 294 generator, 77 load and 467 integration tests, allocation,
reference/maintainability guards, Debug rollback, NativeAOT and desktop codec
compatibility. It still **failed overall** because the unchanged public-API tool
used nonexistent `TypeAttributes.Forwarder`; later package/demo/load steps skipped.

The isolated one-line supporting [draft PR #928](https://github.com/SunSi12138/SharpLink/pull/928)
uses the supported `ExportedType.IsForwarder` property. No reviewed API inventory,
runtime source or validation budget changes. Its exact head
`37b92f20e20ce8a7b751660d56d636a0ce1660e7` passes
[canonical PR Fast](https://github.com/SunSi12138/SharpLink/actions/runs/39637999194)
and [CodeQL](https://github.com/SunSi12138/SharpLink/actions/runs/39637999128).

The [full corrected validation](https://github.com/SunSi12138/SharpLink/actions/runs/39638255855)
is successful, including every previously skipped API/package/trim/AOT/demo/load
step. Its head `57522659fb7705b6aa5464233fb2ccfd18eabc2a` differs from that PR head
only by the validation trigger workflow; production, tests, tools and canonical
workflows/actions are otherwise identical. Browser/WASM jobs are outside this
Extended scope and intentionally skipped, not claimed.

Evidence access is limited: the raw local archives and ownership-prototype
replay bundle are sealed locally, **not published**. Public archive upload was
not authorized by the permission review, including one evidence-backed retry;
it was stopped rather than repackaged or routed elsewhere. The
[public evidence index](../eng/research/cold-control/README.md) distinguishes
original source/model files and fresh CI output from those local-only observations.
No remote ownership-prototype replay is claimed. The evidence branch's own final
CI result must be read separately. Passing unchanged-runtime characterization/model
jobs does not validate the unapplied ownership prototype.

## What remains viable

- A 3.0 lifetime representation that uses existing owners without additional normal
  checks/copies and without extending completed-stream roots, with physical token
  and disposal ownership proved end to end.
- An immutable logical session/domain boundary, if its same-physical-connection
  authentication, stream/codec lifetime and topology semantics are designed and
  measured. A new connection plus reauthentication is not equivalent evidence for
  applications that require the same live connection and old credentials.
- A narrower compatible implementation-update contract with admitted-call overlap,
  separately fixing queued ownership and cold transaction ordering. That may be
  useful, but it is not full #775 exact registration or the strict physical fence.

No production acceptance follows merely from potential feasibility. A next
candidate must close the concrete ownership cases, implement framework-enforced
cohort/commit semantics, preserve existing authentication/capacity/lifecycle checks,
and pass actual broad CI plus representative native/allocation/root comparisons.
No numerical performance threshold is invented here, and nonsignificant timing
differences are not called zero cost.
