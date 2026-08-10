# Runtime Architecture Phase 00 baseline

This baseline freezes lifecycle and performance evidence before the breaking Runtime
architecture stages tracked by #67. Phase 00 changes test, benchmark, tooling, and
documentation code only; `src/**` is intentionally unchanged.

## Deterministic lifecycle entry points

`RuntimeArchitecturePhase00Tests` records race seed `68002026` and runs 100 bounded
repetitions for each new race. It proves:

- `RpcSession` `Fault`, `BeginShutdown`, and `DisposeAsync` converge on one transport
  dispose, while the already-bound RuntimeContext, StreamManager, and input references
  remain stable through termination.
- Pending Response, user cancellation, deadline, disconnect, and GoAway have exactly
  one terminal winner; the pending slot and owner counter return to zero without
  underflow.
- `ManualTimeProvider` advances UTC and monotonic timestamps together and executes due
  timers deterministically, without sleeping. Phase 00 does not inject it into production.

Existing focused tests remain the executable baseline for the other ownership domains:

| Domain | Test entry point |
|---|---|
| Session terminal publication and send/dispose | `RpcSessionLifecycleTests` |
| Send queue admission, flush, owner return | `SendPumpTests` |
| Client/Server builder primary + cleanup failures | `BuilderOwnershipRollbackTests`, `DynamicRollbackTests` |
| Client background task observation and join | `SharpLinkClientBackgroundTaskTests`, `SharpLinkClientLifecycleStateTests` |
| Server framework task observation and join | `SharpLinkServerInvocationTests.FrameworkJoinShouldNotHideAnUnexpectedSiblingFailure` |
| Active call/stream/capacity release | `ServerLifecycleCharacterizationTests`, `IntegrationBehaviorTests` |

The current Session still has post-construction RuntimeContext binding and does not expose
an immutable Role/handshake snapshot. Those are intentionally recorded gaps for Phases 02,
05, and 06, not silently asserted as already-complete target behavior.

`BuilderFaultInjectionProbe` is test-only and records acquisition order, cleanup order, and
per-resource cleanup counts. The static multi-endpoint builder baseline injects a failure at
the second transport profile binding point and proves primary/cleanup exception ordering,
reverse rollback, and exactly-once disposal. Existing direct transport, dynamic resolver,
Server RuntimeContext/listener, generated registration, and dynamic-module rollback tests
cover the remaining acquisition domains. Caller-owned logger factories, service providers,
replacement service instances, codecs, and explicit policies are intentionally excluded
from cleanup ownership.

## Compatibility and deployment evidence

The following existing gates freeze behavior without changing Protocol v2, contract/schema,
or Generated API 4:

```bash
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release \
  --treenode-filter '/*/*/SharpLink.UnitTests.Protocol.ProtocolV2Tests/*'
./eng/verify-protocol-v2-cross-version.sh artifacts/packages
dotnet run --project test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj \
  -c Release -- --timeout 120s
./eng/run-shared-memory-aot-process-smoke.sh
```

`RuntimeAssemblyIntegrationTests` is the collectible ALC entry point. Generated manifest
version rejection lives in `GeneratedManifestCompatibilityTests` and
`Api3BinaryFixtureIntegrationTests`. The Release Gate remains the three-platform AOT and
package authority. Long-running soak/Nightly jobs are deliberately not part of Phase 00.

## Six-path microbenchmark

`RuntimePhase00Benchmarks` contains exactly six unparameterized cases so a short validation
does not grow into a long soak:

1. Unary send/complete over the loopback benchmark environment.
2. PendingRequestTable register/complete.
3. StreamManager register/dispatch/complete with a singleton no-op dispatcher.
4. SendPump enqueue/force-flush against a non-blocking discard writer.
5. Power-of-two-choices normalized-load comparison.
6. Cached codec resolve.

Setup, transport construction, and diagnostic report creation are outside benchmark methods.
Every report includes P50, P99, operations/second, allocation, and thread/lock columns. The
bounded default uses three launches, three warmups, and twelve 100 ms result iterations per
case, providing 36 result samples without becoming a soak. Run a correctness-only pass first,
then the bounded formal baseline on Ubuntu:

```bash
dotnet run -c Release --project test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -- \
  --filter '*RuntimePhase00Benchmarks*' --job Dry --noOverwrite

SHARPLINK_BENCHMARK_SHA="$(git rev-parse HEAD)" \
  ./eng/run-runtime-phase00-baseline.sh \
  artifacts/runtime-phase00-baseline/<fresh-name>
```

The runner captures the exact SHA, UTC timestamp, CPU/OS/memory and .NET environment. A
single absolute number is not an optimization conclusion; later hot-path PRs must run the
same cases on the same Ubuntu host in interleaved base/head order and treat 3%–5% as the
current manual noise envelope.

## Process-global serial inventory

The UnitTests and IntegrationTests assemblies are currently assembly-wide `NotInParallel`.
The known process-global reasons are:

- generated manifest catalog registration/removal;
- bounded static object/operation pools whose retention is asserted;
- process-wide MeterListener/ActivityListener callbacks;
- rollback-plugin environment variables and shared isolation semaphore;
- integration transport registries, ports, pipes, and dynamic collectible ALC fixtures.

Phase 15 owns replacing catalog discovery with an instance source and narrowing this list.
Phase 00 keeps the serial policy visible rather than weakening determinism prematurely.
