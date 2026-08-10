# Runtime Architecture Phase 12: construction boundary

Phase 12 removes the historical long, nullable-topology constructor families from the internal
`SharpLinkClient` and `SharpLinkServer` implementations. A runtime is now created only from an
explicit typed composition that Builder materialization has already completed.

```text
mutable Builder
  -> immutable ClientBuildPlan / ServerBuildPlan
  -> SynchronousBuildTransaction materialization
  -> ClientRuntimeComposition / ServerRuntimeComposition
  -> SharpLinkClient / SharpLinkServer
  -> transaction Commit and ownership transfer
```

There is no compatibility forwarding constructor. Test code uses the same Builder -> BuildPlan ->
Materialize path as production code; `ClientBuilderTestHelper` is only a concise wrapper around the
production multi-cluster child materialization path with an explicit empty manifest snapshot, and
does not construct a runtime directly.

## Construction inventory

| Runtime | Sole constructor input | Materialized by | Explicit ownership carried by the composition |
| --- | --- | --- | --- |
| `SharpLinkClient` | `ClientRuntimeComposition` | `SharpClientBuilder` | direct transport or typed fixed/static/dynamic topology, Runtime Context, frozen manifest/proxy snapshots, protocol/pool/retry/interceptor snapshots, admission policy, reconnect jitter, logger |
| `SharpLinkServer` | `ServerRuntimeComposition` | `SharpLinkServerBuilder` | listener, frozen service registrations, Runtime Context, logger, caller or framework provider, admission controller, shutdown plan, interceptors, manifest snapshot |

Client topology is tagged before the Client exists:

| Builder plan kind | Runtime composition kind | Runtime behavior |
| --- | --- | --- |
| fixed transport or one static endpoint | `FixedClientRuntimeTopologyComposition` | direct pool fast path, optional endpoint diagnostics |
| two or more static endpoints | `StaticClientRuntimeTopologyComposition` | fixed materialized endpoint configurations and cluster options |
| resolver | `DynamicClientRuntimeTopologyComposition` | builder-owned resolver, transport delegate, and frozen cluster options |

The Client receives the composition without interpreting nullable topology arguments. Its one typed
constructor directly binds the already-selected composition topology while the Client itself becomes
the static/dynamic cluster owner, so every returned Client is immediately valid. This binding does
not enumerate caller endpoints, invoke an endpoint factory, select a topology from optional inputs,
fall back to a catalog, clone/default options, or materialize a Runtime Context.

## Removed behavior and migration map

The deleted constructors were internal, but the mapping below is the required migration path for
every former in-repository caller. Configuration now happens before `Build`; no caller supplies a
partially validated runtime state to a constructor.

| Historical constructor input | Replacement Builder configuration / materialization owner |
| --- | --- |
| Client direct `IClientTransportFactory` | `UseTransport`; builder materializes the fixed topology and owns the factory transfer |
| Client one/many endpoint configurations and endpoint factory | `UseEndpoint` / `UseEndpoints`; compile freezes one endpoint snapshot, materialization calls the factory once per frozen endpoint |
| Client dynamic resolver and dynamic transport factory | `UseEndpointResolver` / `UseDnsEndpoints`; builder binds the resolver's `TimeProvider` before runtime construction |
| Client heartbeat, request timeout, session flush, pool | `UseHeartbeat`, `UseRequestTimeout` / `DisableRequestTimeout`, `UseRpcSessionFlush`, `UseConnectionPool` |
| Client runtime context, protocol, serializer/codec, logger | `UseRuntime`, `UseTimeProvider`, `UseProtocol`, `UseSerializer` / `UseCodec`, `UseLoggerFactory` |
| Client auth, interceptors, cluster selection, retry/admission | `UseAuthenticator`, `AddInterceptor`, `UseCluster` / `UseLoadBalancing` / `UseEndpointSelector`, `UseRetry`, `UseEndpointAdmission` / `UseCircuitBreaker` |
| Client nullable topology choice, catalog fallback, reconnect jitter default | frozen `ClientTopologyPlan`, `SharpLinkGeneratedManifestSource`, and Builder-owned reconnect strategy; only the internal deterministic-test setting can replace jitter |
| Server listener, heartbeat, session flush, logger | `UseTransport`, `UseHeartbeat`, `UseRpcSessionFlush`, `UseLoggerFactory` |
| Server runtime context, protocol, serializer/codec | `UseRuntime`, `UseTimeProvider`, `UseProtocol`, `UseSerializer` / `UseCodec` |
| Server auth, interceptors, exception mapper | `UseAuthenticator`, `RequireAuthentication`, `AddInterceptor`, `UseExceptionMapper` / `EnableDetailedErrors` |
| Server provider and services | `UseServiceProvider`, automatic registration, `DisableAutomaticServiceRegistration`, `EnableService`, `ExcludeService`, `ReplaceService` |
| Server admission, manifest snapshot, provider/service cleanup, shutdown plan | Builder compile/materialization and its transaction; `ServerServiceCleanup` is prebuilt before the Server exists and the current fixed shutdown policy is `ServerShutdownPlan.Default` |
| direct unit-test runtime construction | production Builder helper paths; tests no longer call a Client/Server constructor |

Use the Builder APIs for all new code:

```csharp
await using var client = SharpClientBuilder.Create()
    .UseTransport(transport)
    .UseRetry()
    .Build();

await using var server = SharpLinkServerBuilder.Create()
    .UseTransport(listener)
    .UseServiceProvider(services)
    .Build();
```

## API surface review

No public constructor was removed: both concrete runtime types are internal implementation types.
The repository has no tracked PublicAPI/APICompat baseline or generated API-surface snapshot to
update; the source inventory was explicitly reviewed instead. The only removed surface is internal
source/binary construction surface, intentionally breaking without an obsolete shim. Protocol v2,
generated API 4, manifest, trimming, and NativeAOT behavior do not change in this phase.

## Boundary checks and focused evidence

The source gate is intentionally simple and reproducible:

```text
rg -n 'new SharpLinkClient\\(|new SharpLinkServer\\(' --glob '*.cs' .
```

The expected result contains only `SharpClientBuilder` and `SharpLinkServerBuilder` materialization
sites. `SharpLinkClient.cs` and `SharpLinkServer.cs` each expose only their single typed-composition
constructor; neither contains catalog fallback, Runtime Context materialization, endpoint source
enumeration, a reflection constructor adapter, or a legacy long-parameter forwarding overload.

Focused local Debug evidence:

- `BuildPlanBuilderTests` covers frozen plans, one-shot endpoint enumeration, deferred factory
  creation, Client fixed/static/dynamic topology configuration, Server plan immutability, and
  materialization rollback ownership.
- `BuilderOwnershipRollbackTests` covers reverse-order exact-once Client/Server resource cleanup
  across final construction failures, Runtime Context failures, factory failures, provider and
  registration paths.
- `SharpLinkClientLifecycleStateTests` exercises fixed, static, and dynamic runtime topologies after
  migration through the real Builder path, including deterministic reconnect jitter.
- `SharpLinkClientRetryTests`, call-options, timeout, cancellation, background-task, late-response,
  and `ServiceRegistrationTests` all use Builder-created runtimes.

The coordinated remote Release, AOT, Chaos, stress, and performance gates are deliberately not run
by Phase 12. They remain serialized under the owning validation task.
