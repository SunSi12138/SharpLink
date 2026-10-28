# #732 M1: cached complete descriptor, locally validated

M1 is a focused representation candidate, not a performance acceptance. The historical M0 campaign remains **No-Go**: the adverse TCP/interceptor/declared-timed one-item, concurrency-1 cell had paired mean-time geometric ratio 1.145 and P99 ratio 1.668 against pre-M baseline. No network timing campaign was rerun.

## Source boundary

M0 is fixed locally at `37548b7a35d50803212d2b0a637306636cc0a1d3`, parent `a6bd0352a931d5c2816e42778055e90efb01144b`, on `experiment/issue-732-complete-method-facts`. The original source archive, binary arms, failed tests, raw samples and reports remain in that worktree's `artifacts/method-facts/`.

M1 is in `/Users/sunsi/.codex/worktrees/issue-732-method-facts-m1/SharpLink`, branch `codex/issue-732-method-facts-m1`, directly based on that M0 commit. The only production edit is `src/SharpLink.Abstractions/RpcMethodFacts.cs`:

- Replace packed scalar storage with one private readonly complete `RpcMethodDescriptor` and a readonly byte for known/cancellation flags.
- Preserve the typed constructor and its validation. Construct the descriptor once after validation; scalar properties delegate to it, and `ToDescriptor` checks known state then returns a value copy.
- Preserve unknown/invalid sentinels, nullable timeout presence and full ticks, 127 client streams, cancellation, idempotency, and response nullability.

The resolver, generated stubs, queue ownership, original-generation leases, cancellation-state pool, public API signatures, API5 identity and wire are unchanged from M0. The rejected pool experiment is not included.

## Actual M1 representation evidence

On arm64, SDK 10.0.102, runtime .NET 10.0.11:

| Measurement | Frozen M0 | Production M1 |
|---|---:|---:|
| Descriptor value size | 32 B | 32 B |
| Descriptor contains managed references | false | false |
| Facts object bytes | 48 B | 56 B |
| `ToDescriptor` IL | 85 B | 26 B |
| FullOpts ARM64 function, including error path | 344 B | 136 B |

M1's success path checks the known bit and copies the descriptor with two paired 128-bit loads/stores. Native code is an explicit non-tiered FullOpts diagnostic with **No PGO data**, not timed evidence. M0 code evidence is reused from its frozen mechanism report. The actual M1 object has exactly two readonly fields: the descriptor and byte flags. The 8 B/method increase also implies 16 B more for the two one-time host sentinel objects.

The allocation probe preallocates 10,000 escaping slots and warms the same allocation helper before measuring: 560,000 B total. Its first un-warmed loop recorded 560,152 B and is retained; the report does not truncate that overhead into an exact measurement. Sources and both outputs are in `artifacts/method-facts-m1/inspect/`, `inspect.json`, and `inspect-warmed.json`; actual native output is `jit-fullopts-diagnostic.log`.

## Request-id and token association, outside timing

`eng/method-facts-preflight/` is a separate diagnostic service. It holds a real request in a synchronous enumerator, waits for an explicit blocked signal, then uses the interceptor's active request id to query the server's live cancellation map. All four cases passed on each of frozen pre-M A, frozen M0 B, and actual M1: TCP/SharedMemory × declared-timed/untimed.

For timed calls, the map entry's request id matches the active request, and its invocation token equals both the business method argument and interceptor token, but differs from the connection token. For the untimed synchronous control, there is no matching state; the context token is nevertheless cancellable and equals the connection token. This demonstrates why `CanBeCanceled` alone is not proof of per-call state ownership.

The initial probe accidentally captured the stream enumerator's linked lifetime token. A subsequent async gate itself caused the ordinary asynchronous dispatch continuation to allocate state for the untimed case. Those failures remain in the logs; the corrected probe captures the business argument before enumeration and blocks the synchronous enumerator, matching the original measured service shape. Arbitrary asynchronously suspended untimed streams can require tracking state.

The M0 report and network table now point to `evidence/TRACKING-ERRATUM.md`. Raw historical data and frozen probe source were not altered. Verified outputs here are `preflight-A-verified.json`, `preflight-B-verified.json`, and `preflight-M1.json`, each with actual server MVID and ABI identity. The timed campaign's service loop was not instrumented.

## Focused semantic validation

Unit and Integration project Release builds: **0 warnings / 0 errors**. Direct TUnit application execution used repository-compatible segment filters:

```sh
dotnet test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/(RpcMethodFactsTests)|(GeneratedCompleteMethodFactsTests)/*'
# Passed: 9, Failed: 0, Skipped: 0

dotnet test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/SharpLinkServerInvocationTests/(EveryCallShapeShouldResolveCompleteFactsOnce)|(ExceptionProjectionShouldNotResolveMethodAgain)|(UnknownOrInvalidFactsShouldRejectBeforeInvocation)|(QueuedContinuationShouldReuseFactsAndOriginalRegistration)|(UntimedStaticServerStreamMustNotRentStateOnlyForFacts)|(SuspendedStreamFailureShouldProjectOriginalFactsAfterRegistryChange)|(EnabledTelemetryAndImmediateAdmissionShouldUseOneResolvedIdentity)|(AdmissionRejectionShouldResolveOnceWithoutEnteringBusinessCode)'
# Passed: 22, Failed: 0, Skipped: 0

dotnet test/SharpLink.IntegrationTests/bin/Release/net10.0/SharpLink.IntegrationTests.dll \
  --treenode-filter '/*/*/RuntimeAssemblyIntegrationTests/RetainedCompleteFactsShouldNotRootRetiredCollectibleGeneration'
# Passed: 2, Failed: 0, Skipped: 0
```

| Requirement | Evidence |
|---|---|
| Complete readonly descriptor and known/cancellation representation | `RetainableFactsShouldContainOnlyReadonlyHostScalars`; recursive readonly/value checks and `IsReferenceOrContainsReferences<RpcMethodDescriptor>() == false` |
| Constructor validation and complete timeout semantics | `ContradictoryFactsShouldNeverDescribeAKnownMethod`, `StreamCountOutsideSupportedRangeShouldFailAtConstruction`, `CompleteFactsShouldProjectEveryScalarWithoutLosingTimeoutPresence` |
| All five shapes and independent value copies | `CachedDescriptorShouldPreserveEveryShapeAndReturnIndependentValueCopies`: 25 kind/timeout combinations, every public scalar, mutated returned copy does not change stored facts |
| Once-only resolution and observer reuse | `EveryCallShapeShouldResolveCompleteFactsOnce` (10 cases), `ExceptionProjectionShouldNotResolveMethodAgain`, `EnabledTelemetryAndImmediateAdmissionShouldUseOneResolvedIdentity` |
| Weak queued selection and original-generation continuation | Existing `QueuedContinuationShouldReuseFactsAndOriginalRegistration` and `SuspendedStreamFailureShouldProjectOriginalFactsAfterRegistryChange` pass on M1; queue production code is unchanged |
| No facts-only call state | `UntimedStaticServerStreamMustNotRentStateOnlyForFacts` plus actual network preflight's synchronous no-state control |
| Retained facts do not root a collectible generation | `RetainedCompleteFactsShouldNotRootRetiredCollectibleGeneration(false)` and retained-stub negative control `(true)` both pass |

Logs and binlogs are in `artifacts/method-facts-m1/`. A first native `dotnet test` command mistakenly used a cross-path OR filter; it selected broader tests instead of the intended subset. That run was stopped, with logs retained in `unit.log`; it is not a valid focused run or a green full-suite claim. Before/around cancellation it reported the four known reader workload timeouts, a client timeout test, and a cancellation-affected telemetry test. Its broader failures are not erased by the focused green runs and require the release owner's combined-head validation if those paths are used for acceptance.

## Next decision

Independent review can assess the M0 snapshot and isolated M1 child diff now. The proposed four-cell, AA/BB-controlled causal campaign is in `issue-732-m1-ablation-plan.md`; it has not been executed. #931 combined-head validation and the original unexplained BootstrapManifest timeout remain release dependencies. No push, PR, merge, or release acceptance occurred in this work.
