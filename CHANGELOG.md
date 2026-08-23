# 更新日志

本文档记录项目的重要变更，格式参考 Keep a Changelog，并遵循语义化版本。

## [Unreleased]

### Changed

- The send pump now wakes through one reusable zero-allocation signal (a claim-token `IValueTaskSource`) instead of racing two channel reads with `Task.WhenAny`. The dual-read wake-up created two `AsTask` wrappers, a `WhenAny` promise, and continuation closures on every pump wake; the signal-based wake allocates nothing per wake and keeps the dual-queue protocol-progress isolation intact.


- The `Throughput` performance profile now flushes its TimedBatch as soon as the outbound queue drains instead of always waiting out the profile's 1 ms batching deadline. Under continuous RPC load the deadline wait made both peers' batch windows interlock into a low-throughput ping-pong (about a third of the `Balanced` QPS at c128); queue-drain flushing keeps the large 64 KiB coalescing threshold while frames of an active pipeline leave immediately. Callers that need deadline-bounded batching configure an explicit `RpcSessionFlushOptions.MaxLatency`, which still drives the deadline wait exactly as before.

### Added

- Client readiness snapshots now expose lifecycle state, active/ready endpoint counts, ready connection count, and the current convergence target. Built-in fixed, static, and resolver topologies support caller-selected endpoint thresholds without raising configured convergence targets or changing `ConnectAsync` connectivity semantics.
- Runtime sessions now receive one immutable creation snapshot containing their Client/Server role, real Runtime Context, and flush policy. Context-derived protocol limits and the sole StreamManager instance are established before the constructor returns.
- `PendingRequestTable` now requires an explicit capacity, codec provider, pending-call owner, and time provider; Client connections supply the dependency set from their Runtime Context without transferring ownership.
- Added the Runtime Architecture Phase 00 deterministic lifecycle/fake-time fixtures and a bounded six-path BenchmarkDotNet baseline. This is test/tooling evidence only and does not change production behavior, Protocol v2, contract/schema identity, or Generated API 4.
- Generated Server API 4 introduces `IRpcGeneratedServerBridge`. Generated stubs now use this narrow operation-lifecycle bridge for inbound and outbound streams, while Runtime exclusively owns dispatchers, flow control, frame construction, send-pump behavior, and terminal arbitration.
- Generated assembly locators now carry the manifest type, Generated API, Protocol version, and Generator version without materializing the manifest. Runtime uses that metadata to reject incompatible dynamic modules before publishing contracts, services, proxies, codecs, adapter scopes, or module leases.
- Release gates cover mixed Generator/package rejection, the SharpLink 2.0 Protocol v2 process pair, minor-4 TimeBudget handshake-floor tests, five NativeAOT call shapes, generated-assembly metadata dependency scans, and collectible API 4 dynamic modules. Pre-2.0 process interoperability is intentionally outside the 2.0 release gate.
- SharpLink 2.0 defines Generated API 4 as the single release ABI bump from the published
  1.1.1/API 3 baseline. Intermediate development-only ABI numbers are not compatibility boundaries
  and are not accumulated into the release version. API 3 artifacts are rejected at
  load/registration/startup with an expected/actual version mismatch and a regenerate-and-rebuild
  action before manifest materialization or runtime publication. Contract/schema identity remains
  unchanged, while Protocol v2 minor 4 intentionally changes request lifetime bytes from an
  absolute UTC deadline to a remaining TimeBudget duration.

### Changed

- Client and Server construction now compiles one immutable build plan before materializing runtime
  resources through a synchronous ownership transaction. Static endpoint and manifest sources are
  snapshotted once; multi-cluster child budget checks materialize the same compiled plan instead of
  using a mutable preflight cache.
- Business exception mapping now belongs to the Server invocation layer. A per-connection generated bridge maps Unary and streaming failures before Runtime encodes a structured protocol error; `RpcSession` no longer stores mapper policy or service/contract/method mapping state.
- `RpcSession` now owns exactly one non-null `ITransportConnection`. Input, output, endpoints, physical cleanup, and terminal connectivity all flow through that transport; Fault and explicit disposal converge on one supervised dispose task.
- Client, static/dynamic cluster, and Server connection paths now construct complete `RpcSession` instances before handshake. Runtime Context, role-specific telemetry, and StreamManager state are read-only for the Session lifetime; stream dispatcher codec-provider overloads also require an explicit provider.
- SharpLink 2.0 generates only API 4 manifests and Runtime accepts only Generated API 4 with Protocol 2. Generated stubs receive codecs when they are constructed, write responses through `IBufferWriter<byte>`, and no longer reference `SharpLink.Runtime`, `IRpcSession`, pooled stream dispatchers, or Runtime helper methods.
- `SharpLink.Sdk` now depends only on `SharpLink.Abstractions` and carries the Analyzer/Source Generator. A contract-only project no longer receives `SharpLink.Runtime` transitively; Client, Server, and Hosting applications continue to obtain Runtime from their corresponding application packages.
- `SharpLink.Hosting` now declares its direct `SharpLink.Runtime` dependency instead of relying on Client or Server to provide the assembly transitively for `IAnonymousPipeAllocatorAccessor`.
- `SharpLink.Abstractions` no longer carries the unused `Microsoft.Extensions.DependencyInjection.Abstractions` package. Consumers that use Microsoft DI APIs must reference that package explicitly; the public BCL `System.IServiceProvider` activator signature is unchanged.
- Public SharpPack adapter types now use the `SharpLink.Serializer.SharpPack` namespace instead of `SharpLink.Runtime`.

### Breaking

- Protocol v2 minor 4 is the SharpLink 2.0 wire baseline for RPC lifetime propagation. Request frames carry remaining `TimeBudget` instead of an absolute Unix-millisecond deadline, and 2.0 rejects peers below minor 4 during handshake so legacy bytes cannot be misinterpreted. Pre-2.0 process interoperability is not a 2.0 compatibility requirement.
- `IRpcSession`, `IStreamManager`, raw stream dispatcher interfaces, public
  `PooledAsyncStreamDispatcher<T>`, public `RpcSession`, public `StreamManager`, and public
  `RpcSessionExtensions` have been removed from the business API.
  Custom transports continue to use `ITransportConnection` through transport factories/listeners;
  generated stubs continue to use the narrow API 4 `IRpcGeneratedServerBridge`. No compatibility
  adapter or legacy Session control path is provided. See
  [`doc/runtime-phase-16-engine-api.md`](doc/runtime-phase-16-engine-api.md).
- `SharpClientBuilder` and `SharpLinkServerBuilder` are single-use. After any Build attempt, whether
  it succeeds or fails, create a new builder; subsequent Build or configuration calls throw
  `InvalidOperationException("This SharpLink builder has already been consumed.")`. Client topology
  configuration is also single-choice: mixing or repeating `UseTransport`, `UseEndpoint(s)`, and
  `UseEndpointResolver` now fails at the second configuration call instead of replacing or delaying
  validation until Build. See [`doc/runtime-phase-11-build-plan.md`](doc/runtime-phase-11-build-plan.md).
- Public `RpcSession` error-send extensions now accept only an already structured `SharpLinkException`; callers that use these low-level protocol helpers must map arbitrary exceptions before encoding them.
- The PipeReader/PipeWriter/disconnect/isConnected `RpcSession` constructor is removed without an obsolete or forwarding shim. Custom transports must implement `ITransportConnection` and expose themselves through a client factory or server listener; the Session no longer completes caller-supplied pipelines or invokes lifecycle callbacks.
- The incomplete `RpcSession` constructors and the `BindRuntimeContext` follow-up call are removed instead of retained as forwarding shims. Internal Client/Server construction also requires an already-built Runtime Context; no process-wide Context or codec fallback remains.
- Assemblies generated by SharpLink 1.1.x use Generated API 3 and cannot be loaded into a SharpLink 2.0 process. Rebuild every contract, service, and plugin assembly with the 2.0 SDK after deleting stale `bin` and `obj` outputs, and do not mix 1.1.x and 2.0 SharpLink packages in one process. Intermediate development-only ABI artifacts are outside the release compatibility contract.
- Hand-written `IRpcStub`, generated-manifest descriptor, or manifest-locator implementations must adopt the API 4 bridge, codec-aware stub factory, `IBufferWriter<byte>` response surface, and self-describing locator constructor.

### Compatibility

- Generated ABI (API 4) remains independent from the network version. SharpLink 2.0 uses Protocol v2 minor 4 as its TimeBudget baseline; the release gate validates the 2.0 package pair and does not promise pre-2.0 process interoperability.
- SharpLink 2.0 intentionally has no hidden API 3 compatibility switch, dual Runtime path, or compatibility environment variable. See [`doc/migration.md`](doc/migration.md) for the complete upgrade checklist.

## [1.1.1] - 2026-08-03

### Added

- Servers can configure the independent `FlowControl.MaxConcurrentCallsPerServer` safety limit. The default is a stable 65,536 calls, the validated hard maximum is 1,048,576, and the value is frozen with the rest of the runtime snapshot at `Build()`.
- `SharpLink.LoadTest --operation hold` creates multiple independent clients and fixed connection pools, holds Singleton-observed calls behind a shared gate, and reports attempted, accepted, peak, completed, exhausted, cancelled, failed, released, and post-release health counts.

### Changed

- Server-wide and per-connection call-capacity rejection remain wire-compatible `ResourceExhausted` results but now have distinct diagnostic messages. A bounded one-byte discriminator survives even the smallest valid error-message limit, new clients restore it while retaining legacy message recognition, and the `sharplink.resource_exhausted` metric adds the low-cardinality `rpc.sharplink.resource_exhaustion_reason` tag for server call, per-connection call, admission, pending-request, and send-queue capacity sources.
- Server startup logs the effective per-connection and per-server call limits through `LogEvents.Server.CallCapacityConfigured`; capacity rejection continues to preserve healthy connections for reuse after slots are released.

### Fixed

- Server applications now emit deterministic static bootstrap calls for referenced generated service manifests. A normal Server-to-Service project reference roots and registers even an internal service implementation before `Build()` snapshots the catalog, without marker types, runtime assembly scanning, or reflection discovery; the path is covered by clean-package, JIT, and NativeAOT process smokes.
- Server-stream failures caused by deadline, remote cancellation, module drain, Server stop, or connection closure now preserve the call state's first terminal reason instead of remapping every `OperationCanceledException` to `Cancelled`. Forced Server stop therefore remains `Unavailable` or `ConnectionClosed`, while `Cancelled` continues to identify caller cancellation or consumer abandonment.
- Server connection shutdown now first signals terminal stream and send-pump state, then cancels and joins the session read loop before completing its `PipeReader`. The handshake and request parsers stop consuming an already-buffered batch as soon as the session becomes terminal, so rolling restart can neither strand bounded stream dispatch, spend the cleanup budget draining stale frames, nor reclaim a live `ReadOnlySequence` while it is being parsed. This eliminates teardown timeouts and the resulting `ArgumentOutOfRangeException` without hiding malformed frames on active sessions.
- Stream-backed client transports now join release of an outstanding `ReadResult` before completing their `PipeReader` and disposing the owned stream. Client handshake and response parsers also stop consuming buffered frames after terminal cancellation, so heartbeat, send-pump, reconnect, and explicit-disposal paths cannot reclaim a live `ReadOnlySequence` while dispatching a final response or `StreamComplete` during rolling restart. Client and server handshake parsers now release every successful read in a `finally` block, preventing malformed handshake payloads from deadlocking connection cleanup.
- Early response-stream disposal now joins the pending terminal callback before returning to its caller. If a remote `StreamComplete` wins while the local dispatcher is closing, every accepted byte is credited and an ordered late `Cancel` reclaims frames intentionally dropped after the close; completed send-state capacity can no longer remain stranded until a later stream stalls. A constrained eight-slot, 10,000-stream regression covers the race without changing public APIs, wire bytes, or production limits.
- The LoadTest OneWay backpressure stage yields after expected `ResourceExhausted` rejections, so high-concurrency synchronous workers cannot starve the stage timer or remain unstarted past the bounded shutdown window. The unsaturated success path and all request-response loops retain their existing scheduling.
- Fixed-queue formal OneWay measurements now retry only local `send_queue_capacity` backpressure, include the wait in logical-send latency, and report the retry count separately. The profile-default backpressure diagnostic still records every raw rejection, while server-side capacity exhaustion remains a formal failure.

### Compatibility

- The server-wide limit is additive and defaults to 65,536. Existing per-connection limits, public contract IDs, Protocol v2 framing, generated code, and transport behavior remain compatible.
- Referenced-manifest bootstrapping adds deterministic generated startup calls without reflection discovery or wire changes, and the runtime continues to recognize manifests generated by SharpLink 1.1.0.
- Stream cancellation classification changes only the structured error code selected for an already-failing call; it adds no public API, generated contract, or valid wire-format change.

## [1.1.0] - 2026-08-02

### Added

- Multi-cluster coordinators can atomically add, replace, and remove complete runtime slots through Client-package extension methods that reuse the full `SharpClientBuilder` surface. Ready coordinators connect candidates before publication, replacements transactionally migrate dynamic registrations, and all public cluster/route/budget reads now share one immutable snapshot.
- Runtime slot mutations enforce `MaxClusters`, steady and bounded transition connection budgets, serialize with shutdown and dynamic assembly lifecycle work, preserve the existing one-time Proxy routing model, and expose structured logs plus `sharplink.client.multicluster.*` metrics.

### Compatibility

- The new Client APIs are additive. Existing static multi-cluster configuration, generated contract IDs, Protocol v2 bytes, server behavior, and one-time proxy routing remain unchanged; cluster identifiers are not added to the wire.

## [1.0.1] - 2026-08-02

### Fixed

- A standalone contract project can now reference only `SharpLink.Sdk`. The package brings the required runtime and abstractions transitively, so generated Proxy, Stub, Codec, and Manifest sources no longer fail with missing SharpLink namespaces or types.
- Contract marker types now live in `SharpLink.Abstractions` while retaining the `SharpLink.Sdk` namespace. `SharpLink.Sdk` forwards every type published by 1.0.0, preserving existing compiled consumers without introducing a NuGet dependency cycle.

### Changed

- The README multi-cluster client section is now fully localized in Chinese, and the package graph, separated-contract guidance, architecture, and release order reflect the SDK-only contract reference model.
- All SharpLink NuGet packages now include the transparent SharpLink icon, which is also displayed in the README.
- Runtime and build dependencies are refreshed together: Microsoft.Extensions, System.Text.Json, and System.Threading.RateLimiting move to 10.0.10, while Microsoft.CodeAnalysis.Analyzers moves to 5.6.0. The source-generator compiler API and TUnit remain pinned to their verified SDK-compatible baselines after the proposed major updates failed the release gate.
- GitHub Actions remain pinned to exact commits while moving to checkout 7.0.1, setup-dotnet 6.0.0, upload-artifact 7.0.1, and download-artifact 8.0.1.

### Compatibility

- Protocol v2 bytes, generated contract IDs, runtime routing, transport behavior, and public type names are unchanged. This patch repairs package dependency closure, preserves old assembly references through type forwarding, updates release assets and documentation, and refreshes compatible build/runtime dependencies.

## [1.0.0] - 2026-08-02

### Highlights

- SharpLink 1.0 establishes the stable Protocol v2 minor-3, generated contract/codec surface, seven-package NuGet graph, transport-independent connection lanes, resilience, security, hosting, multi-cluster routing, dynamic modules, streaming, and NativeAOT support developed throughout the 0.7/0.8 series and release candidates.
- The final performance matrix records SharpLink leads of 21.3%, 58.2%, and 12.6% over grpc-dotnet in the three primary published scenarios, 1.95 million QPS at four-server scale, and throughput comparable to gRPC C++ in the closest local Duplex A/B. See [the concise performance and stability report](doc/performance.md) for workload boundaries and exact results.
- A 24-hour cross-machine mixed-load run completed 414,775,951 successful operations with zero RPC or payload-validation errors. Process restart recovery and cross-region capacity checks also completed with zero content errors.

### Compatibility and validation

- The stable release changes only version metadata and documentation from RC7; runtime code, public API, generated contracts, valid Protocol v2 bytes, package graph, transport behavior, and defaults are unchanged.
- The tested product candidate is commit `36a80656be91822556942a2841750ba8555d2ead`. Release packaging and CI verify that every stable package is built from the final tagged commit, carries that repository identity, includes XML documentation and portable symbols, and passes clean-cache package consumption.

## [1.0.0-rc7] - 2026-07-30

### Fixed

- Pooled stream dispatchers now clear the previous lease while the object is still marked returned, then atomically activate the new lease. A delayed return callback can no longer land between activation and reset, republish the just-rented dispatcher, and hand one active response stream to two RPC calls as `Only single consumer is supported`.

### Compatibility

- Public API, generated contracts, valid Protocol v2 bytes, package graph, transport behavior, connection-pool defaults, and allocation shape are unchanged. The fix reorders the existing reset and lease compare/exchange; it adds no lock, allocation, or new transport/lane abstraction.
- Fixed 1/4/16-connection validation confirms that TCP, UDS, NamedPipe, and SharedMemory already use the transport-independent connection pool as independent ordered lanes. Adaptive 1/4 pools converge to fixed 4/4 throughput after warmup, so RC7 does not add a second pooling abstraction.

### Validation

- Exact RC6 reproduced the same dispatcher failure in two independent Linux x64 runs, including one failure among 18,187,330 attempted high-churn duplex streams and one failure in the longer paired A/B set. A deterministic blocked-reset regression fails on the old ordering in 10 ms and passes on RC7.
- The RC7 candidate completed 35,858,349 validated UDS streams in 60 seconds, then another 36,192,551 validated streams across 15-second TCP, UDS, NamedPipe, and SharedMemory runs, with zero transport or payload-validation failures. Post-fix fixed 1/4/16 runs also passed on all four transports.
- Five longer adjacent RC6/candidate pairs used UDS, Server GC, Throughput profile, c128, four fixed connections, 4096 bytes × 8 messages, and alternating order. Candidate medians changed QPS -1.56%, P99 +1.06%, CPU/stream +1.90%, and allocation/stream +0.05%; pairwise QPS ranged from -5.27% to +4.96%, so no change exceeded the measured noise band. Three high-churn pairs changed median QPS -0.76% and P99 -0.85%.
- Release builds completed with zero warnings/errors on macOS arm64 and Linux x64. Unit 513/513, Generator 121/121, Integration 252/252, and validated-Duplex 21/21 pass on both architectures.

## [1.0.0-rc6] - 2026-07-30

### Performance

- Stream-backed transports now read into 16 KiB PipeReader blocks instead of the 4 KiB framework default. Common 4096-byte business payload frames no longer cross nearly every receive segment, reducing segmented SharpPack reads without changing Protocol v2 bytes, RPC ordering, flow control, connection-pool defaults, or the public API.
- `SharpLink.StreamLoadTest` adds a validated `duplex-equivalent` lane with exact message byte/count controls, operation-ID/order/full-payload verification, explicit validation/cancellation accounting, message throughput, and directional business MiB/s. A dedicated TUnit project covers corrupt, duplicate, missing, reordered, extra, cancelled, boundary, and generated-RPC round trips.

### Compatibility

- The 16 KiB block is rented from the existing shared PipeReader pool. It adds no per-message allocation and does not parallelize a single ordered byte-stream reader; multi-lane scaling continues to use the transport-independent connection pool.
- Public API, generated contracts, valid Protocol v2 bytes, package graph, and connection defaults are unchanged.

### Validation

- On a Ryzen 9 7950X bare-metal Linux host, five adjacent RC5/candidate pairs used TCP loopback, Server GC, Throughput profile, c128, fixed 1/4/16/64 connections, 4096 bytes × 8 bidirectional messages per stream, and a fixed 64 MiB send queue. Median validated message throughput changed by +27.86%, +23.47%, +3.94%, and +1.08%; median P99 changed by -24.78%, -8.17%, -4.83%, and -9.26%. CPU/message and allocated bytes/message decreased in all four shapes, with zero transport or validation failures across 40 processes.
- Candidate profiles reduced `SharpPackReader.GetNextSpan` exclusive samples from 1.53% to 0.36% at one connection and from 16.39% to 9.42% at 64 connections, confirming the intended segmented-read mechanism.
- Release builds completed with zero warnings/errors on macOS arm64 and Linux x64. Unit 512/512, Generator 121/121, Integration 252/252, and validated-Duplex 21/21 pass; 120-second macOS SharedMemory and Linux TCP Chaos smokes completed 11 rolling restarts each with zero unexpected failures and all resources drained. Independent-process SharedMemory NativeAOT smoke passes on osx-arm64 and linux-x64.

## [1.0.0-rc5] - 2026-07-29

### Fixed

- Pooled response-stream dispatchers now use a monotonic lease generation instead of resetting a reusable leased/returned bit. A delayed return contender from an older stream can no longer commit after the same dispatcher has been rented again, reinsert the active lease into the process-wide pool, clear its codec and callbacks, or expose the active call to another consumer as `Only single consumer is supported`.

### Compatibility

- Public API, generated contracts, Protocol v2 bytes, package graph, and valid stream behavior are unchanged. The fix adds no per-call allocation or lock; it replaces the existing pool-state compare/exchange with a generation-aware 64-bit compare/exchange.

### Validation

- A deterministic two-contender regression fails on exact RC4 with `retained=1, referencesIntact=False`, proving that the delayed old return both repooled and cleared the reused lease. The RC5 candidate passes the same schedule and all 15 dispatcher lifecycle tests.
- Unit 511/511 and Integration 252/252 pass in Release with zero test failures.
- Five alternating exact-RC4/candidate Balanced TCP Duplex pairs (c8, 32 items, 10-second measurements) completed with zero failures. Paired-median candidate deltas were QPS +3.82%, P50 -3.24%, P99 -12.60%, allocation/operation -2.04%, and CPU/operation -2.81%; these local results exclude a measurable regression and are not claimed as a performance improvement.

## [1.0.0-rc4] - 2026-07-28

### Fixed

- Client and Server interceptor continuation-state caches now retain one exclusively owned state per physical thread instead of using mutable process-wide lock-free freelists. Concurrent pass-through interceptors can no longer hit an ABA reuse window that clears or replaces another invocation's owner and surfaces `The interceptor continuation has expired`.
- PR, nightly, and release Integration gates now use fixed single-test execution. Adaptive suite parallelism produced unrelated TCP disconnect/deadline failures under host contention without shortening the 252-test wall time; release acceptance no longer depends on runner resource timing.

### Validation

- Exact RC3 produced 103 continuation-expired failures in one 8-second c32 witness and 38 failures across 3/5 paired baseline processes. The candidate completed all five paired c32 processes and 19,843,630 successful intercepted calls with zero failures; deterministic Client and Server ownership tests each fail independently on exact RC3 and pass on the candidate.
- Five c1 exact-baseline/candidate pairs measured paired-median QPS +0.74%, P50 +2.44% (one-microsecond histogram quantization), P99 -1.25%, allocation/operation -0.006%, and CPU/operation +2.35%. Five c32 pairs measured QPS +2.20%, P50 -1.69%, P99 -2.00%, allocation/operation +0.009%, and CPU/operation -3.33%, excluding a material regression.
- Non-incremental Release built with zero warnings/errors; Generator 121/121, Unit 510/510, focused Interceptor Integration 18/18, and full Integration 252/252 passed.
- Two adaptive-parallel full-suite runs failed in different tests, while each failure passed three focused repetitions and the fixed-serial full suite passed 252/252 in 28.0 seconds, matching the parallel suite's successful/failing wall-time range.

## [1.0.0-rc3] - 2026-07-28

### Fixed

- `SharpLink.PackageSmoke` now composes its restore-time package version directly from `VersionPrefix` and `VersionSuffix`. The previous `$(Version)` default was expanded before the SDK synthesized that property, leaving all four SharpLink `PackageReference` versions empty and making the NuGet release gate fail with `NU1015` for every prerelease candidate.
- Client heartbeat Ping and peer Pong/HealthResponse control frames now wait for bounded send-queue capacity. Sustained OneWay saturation no longer turns a local heartbeat `ResourceExhausted` into a disconnected single-connection pool and a cascade of `Unavailable`; application OneWay sends retain their explicit fail-fast backpressure signal.
- Response/control-frame capacity waiting first uses the original synchronous queue-admission path and creates an asynchronous waiter only when the queue is actually full, preserving normal-path throughput and allocation behavior.
- Dispatch observers treat a `ConnectionClosed` raised while a capacity waiter is released by normal session shutdown as expected termination, while continuing to log internal and unexpected exceptions as errors. Rolling restarts no longer fail Chaos solely because an already-closing session cannot accept its final response.

### Validation

- MSBuild property evaluation reports `SharpLinkPackageVersion=1.0.0-rc3`. A fresh package cache restores exclusively from the locally packed SharpLink feed plus NuGet.org, compiles generated contracts without project references, and runs the package consumer over TCP and SharedMemory.
- Deterministic heartbeat tests prove that a full queue keeps the connection healthy until capacity returns and that Ping, Pong, and HealthResponse retain synchronous completion and exact frames when capacity is available. The Unit suite passes 507/507.
- A focused dispatch-observer test proves that only expected connection closure is suppressed and that internal or ordinary exceptions still produce the original error log. The Unit suite passes 508/508 after this shutdown-path coverage.
- Twelve-second single-connection TCP and SharedMemory OneWay saturation witnesses cross the heartbeat interval with only expected `ResourceExhausted` results and zero `Unavailable`. Five exact RC1/RC3 response-load pairs completed without failures; median QPS changed -0.36%, P50 +0.77%, P99 -0.92%, CPU/operation -0.52%, and allocation/operation +0.02%, excluding a material normal-path regression.
- RC2 was never tagged or published. It is retained locally as the exact response-backpressure fix checkpoint and is superseded because its clean-cache package-consumer gate could not restore the prerelease package graph.

## [1.0.0-rc2] - 2026-07-28

### Fixed

- Server RPC success, service-error, admission-rejection, decode-error, cancellation, and module-drain responses now wait for bounded send-queue capacity instead of allowing a local `ResourceExhausted` enqueue failure to escape synchronous dispatch and terminate the connection. The synchronous fast path remains allocation-free when capacity is available.
- Response backpressure retains the global and per-connection call-admission slots until the response enters the send queue, bounding queued response work instead of accepting unbounded replacement calls while a slow peer is saturated.
- The formal performance matrix fixes both endpoints to the same 64 MiB send queue for normal throughput comparisons and uses payload-aware default concurrency. The dedicated OneWay backpressure workload retains profile defaults and reports saturation separately.

### Validation

- Deterministic full-queue regression tests cover mapped error and successful payload responses, connection health before and after recovery, retained admission while blocked, final resource release, exact response contents, and the non-full synchronous fast path. The full Unit suite passes 505/505 with zero build warnings or errors.
- The original SharedMemory witness at 64 KiB payload and concurrency 128 completed 728,343 calls with 12 client-local default-queue `ResourceExhausted` signals and no `ConnectionClosed`/`Unavailable` cascade. Repeating with the matrix's fixed 64 MiB queue completed 734,065 calls with zero failures.
- RC1 was never tagged or published. It is retained as a local reproducible checkpoint and superseded by RC2 because its default-queue load witness could amplify one server response enqueue failure into connection-wide failures.

## [1.0.0-rc1] - 2026-07-28

### Release candidate

- Froze the documented public API, Protocol v2 minor-3 wire layout, generated contract surface, package graph, feature demos, operational limits, and release process for final scenario, soak, and performance validation.
- Confirmed bidirectional independent-process TCP interoperability between the final 0.8 series and the RC code using generated DTO and scalar calls. Protocol v1 and handshake layouts other than minor 3 remain unsupported.
- Publishes seven consistently versioned libraries with complete XML IntelliSense documentation and matching portable-PDB symbol packages; `SharpLink.Sdk` continues to carry the source generator rather than publishing it as an eighth package.

### Publication status

- This checkpoint is retained locally while the exact-RC performance matrix and long-running soak are completed. No tag, GitHub Release, or NuGet package has been created.
- Public publication additionally requires the repository `release` Environment, NuGet.org Trusted Publishing policy, private vulnerability reporting, Dependabot alerts, and an initial clean CodeQL result described in `doc/releasing.md`.

## [0.9.2] - 2026-07-28

### Fixed

- Separated semantic package versions from numeric CLR/file versions. `1.0.0-rc1` now builds as package/informational version `1.0.0-rc1` with assembly/file version `1.0.0.0`; previously the prerelease suffix caused compiler error `CS7034` and blocked every RC build.
- Replaced the obsolete 0.7.4-only performance workflow and three version-bound evidence scripts with the current transport/profile/payload matrix smoke. The benchmark evidence runner no longer writes to a historical version directory.

### Release engineering and security

- Release builds now emit portable PDBs and all seven package projects produce matching `.snupkg` symbol packages while retaining package XML documentation. Package validation, Source Link source embedding, deterministic CI builds, transitive NuGet auditing, and zero-warning product builds are enforced centrally.
- Added a clean-worktree package verifier for version consistency, exact repository commit, XML documentation, symbol PDBs, and the SDK-embedded Generator. Release Gate uploads verified `.nupkg`/`.snupkg` pairs.
- Added gated NuGet.org OIDC Trusted Publishing. Main-targeting release PRs now automatically satisfy the repository's required `release-summary`; a `v*` tag publishes only after the complete three-platform build/test, NativeAOT, package-smoke, and Chaos gate succeeds and the protected `release` environment approves it. Manual workflow runs never publish and no long-lived API key is stored.
- Added a pinned SDK feature band, security policy and private reporting path, Dependabot configuration, scheduled/PR CodeQL analysis, structured Issue/PR templates, least-privilege and commit-pinned workflow actions, PR concurrency control, and an explicit release/rollback checklist.
- Corrected package copyright metadata and added package-specific descriptions and NativeAOT discovery tags.

### Validation

- A clean-cache 41-project Release rebuild completed with zero warnings/errors; Generator 121/121, Unit 503/503, and Integration 252/252 passed.
- The pre-fix `1.0.0-rc1` build failed with `CS7034`; the fixed probe builds with zero warnings/errors and emits `1.0.0.0` assembly/file versions plus `1.0.0-rc1+<commit>` informational versions.
- Seven `0.9.2` `.nupkg` and seven matching `.snupkg` files passed structural verification, and a fresh-cache package consumer restored, generated code, built, and ran successfully. NuGet reported no known vulnerable or deprecated direct/transitive package in the 41-project solution.

## [0.9.1] - 2026-07-28

### Documentation and demos

- Replaced 301 version-specific architecture, audit, migration, performance, and chaos reports with 18 current topic documents covering setup, contracts/codecs, calls/streaming, transports, security, resilience, admission, hosting, observability, multi-cluster/dynamic modules, limits, troubleshooting, migration, architecture, Protocol v2, load testing, and the pending RC performance baseline.
- Reworked the README documentation index and removed every link to superseded 0.x reports. Local links across README and the complete current documentation set validate without missing targets.
- Added runnable Security, Compression, AdmissionControl, InterceptorsTelemetry, Resilience, TransportMatrix, and MultiCluster demos. The transport matrix executes TCP, NamedPipe, UDS where supported, SharedMemory, and AnonymousPipe; the multi-cluster demo uses two generated contract assemblies and two physical servers to prove compile-time route isolation.

### Compatibility and validation

- Product runtime behavior and public API are unchanged. The performance matrix default output path no longer embeds an obsolete development version.
- The complete 41-project Release solution rebuild passes with zero warnings and errors. All seven new demos execute successfully and assert their advertised behavior, including negotiated bidirectional compression, overload rejection, ActivitySource emission, two-endpoint routing, five transports, and two generated cluster routes.

## [0.9.0] - 2026-07-28

### Documentation

- Every public API in the published framework source now has compiler-validated XML documentation; CS1591 is an error for `src/` projects while tests, demos, and generated test contracts remain outside the product API gate.
- All seven runtime NuGet packages now include their matching XML documentation file for IDE IntelliSense. Generator and SDK APIs are covered by the same source-build gate.
- Corrected invalid existing XML parameter references and documented protocol values, error identities, authentication/authorization contracts, builders, transports, streaming lifecycle, sessions, and hosting accessors with behavior-specific guidance.

### Compatibility and validation

- Runtime behavior, public signatures, Protocol v2 bytes, generated identifiers, and hot paths are unchanged; this checkpoint changes comments and build/package policy only.
- The compiler-backed pre-fix witness contained 266 unique missing public members across published source projects; the final count is zero with a non-incremental Release build at zero warnings and zero errors.
- Generator 121/121, Unit 503/503, and Integration 252/252 passed. All seven `0.9.0` packages were inspected and contain their corresponding `lib/net10.0/*.xml` documentation file.

## [0.8.44] - 2026-07-28

### Fixed

- Server session/framework joins, Client background-worker joins, and static endpoint-cluster worker joins inspect every fault in each tracked task and preserve unexpected sibling failures instead of trusting the single exception selected by `await Task.WhenAll`.
- Server synchronous dispatch releases call admission, request state, service leases, and response writers even when a terminal cancellation/error response is rejected by the bounded send queue.
- Rejected `StreamComplete` and `StreamError` frames still close their local send-flow state, preventing failed terminal enqueues from retaining `MaxConcurrentStreamsPerConnection` slots.

### Compatibility and validation

- Public API, valid Protocol v2 framing, method/field IDs, payload layouts, and successful call behavior are unchanged. Stop may now surface previously hidden unexpected background failures; bounded-queue terminal failures retain their original exception while cleanup completes.
- The independent-root-cause policy counts the three shutdown-join manifestations once. This release contains three actual engineering root causes and does not pad the batch with duplicate call sites, theoretical races, defensive-only edits, or syntax modernization.
- Exact-0.8.43/candidate Balanced TCP streaming completed without failures. A five-pair, 10-second c2s follow-up measured paired-median QPS -0.05%, P50 -0.19%, P99 +0.27%, and CPU/operation -0.38%, excluding a measurable hot-path regression.
- Non-incremental Release built with zero warnings/errors; Generator 121/121, Unit 503/503, Integration 252/252, 120-second shared-memory Chaos, independent-process SharedMemory NativeAOT, seven-package pack, and fresh-cache PackageSmoke passed.

## [0.8.43] - 2026-07-27

### Fixed

- Shared-memory mapping creation serializes local cleanup/initialization and preserves fresh peer files, preventing concurrent creators from unlinking a live mapping while retaining cleanup of abandoned files.
- Streaming receive-credit draining bypasses the flow-control gate when no cross-stream update exists and returns an emptied queue to the null fast path, removing a redundant lock per received item.
- Pending calls completed as `ConnectionClosed` without an explicit exception retain that structured error code instead of falling back to `Internal`.
- Disposing a Client response stream before its terminal result marks the call activity as Error and records `consumer_abandoned` instead of reporting successful completion.
- A stale dynamic endpoint selection that resumes after generation release re-retires any lazily recreated admission-policy state, preventing per-generation circuit-breaker state from accumulating during topology churn.

### Compatibility and validation

- Public API, valid Protocol v2 bytes, method/field IDs, payload layouts, and normal endpoint selection are unchanged. Shared-memory cleanup now requires a file to be at least one minute old; explicitly stale files remain reclaimable.
- Non-incremental Release built with zero warnings/errors; Generator 121/121, Unit 496/496, Integration 252/252, 120-second shared-memory Chaos, and NativeAOT TCP passed. The seven-package pack and fresh-cache TCP/shared-memory package smoke are part of the final local gate.
- Three alternating exact-0.8.42/candidate Balanced stream pairs completed with zero failures. Paired-median throughput changed by +1.5% c2s, -1.8% s2c, and +4.0% duplex; the unary control changed by -0.6%, with no material latency regression. The isolated 0.7.11/current investigation localized the historical duplex loss to the redundant flow-control lock and measured +6.7% causal median recovery.

## [0.8.42] - 2026-07-27

### Fixed

- Throughput timed batching retains a single outstanding Channel read across flush deadlines instead of repeatedly cancelling it, preventing a producer/cancellation race from terminating the process.
- Non-nullable `Memory<T>` and `ReadOnlyMemory<T>` Codecs reject the collection null marker as `DataLoss`; nullable array/list and default `ImmutableArray<T>` shapes retain their existing representations.
- Fixed-width nullable primitive Codecs reject non-zero ignored value bytes after a null marker, while canonical null and present-value decoding remain allocation-free.
- Protocol v2 cancel/health and handshake writers classify invalid local enum/limit arguments as argument errors before advancing the writer; readers continue to classify peer input as `ProtocolViolation`.
- Generated DTO runtime Codec schema identity includes nullable member annotations while preserving established non-nullable schema identities.

### Compatibility and validation

- Valid Protocol v2 framing, method IDs, field IDs, payload bytes, and non-nullable DTO schema identities are unchanged. Previously accepted non-canonical nullable null bodies and null markers for non-nullable memory shapes now fail as `DataLoss`.
- Exact 0.8.41 Throughput streaming exited 134 in both independent `operation=all` repetitions and in 3/5 s2c plus 5/5 c2s processes; the candidate completed 16/16 processes and 64/64 unary/streaming stages without failure.
- Non-incremental Release built with zero warnings/errors; Generator 121/121, Unit 493/493, Integration 250/250, 120-second shared-memory Chaos, NativeAOT TCP, seven-package pack, and fresh-cache TCP/shared-memory package smoke passed. Ten-process exact-baseline/candidate TCP unary medians changed by -0.76% with stable P50/P99 and slightly lower allocation; nullable present decode improved 5.155 to 5.090 ns/op with zero allocation.

## [0.8.41] - 2026-07-27

### Fixed

- Required unary and client-streaming responses now reject a null value decoded by a custom or mismatched codec as structured `DataLoss`; explicitly nullable responses remain valid.
- Required ServerStreaming/DuplexStreaming response items and ClientStreaming/DuplexStreaming request items now enforce their generated nullability contract at the shared stream dispatcher boundary.
- Runtime method fingerprints now include nullable response identity, preventing separately generated required and nullable contracts from appearing compatible while preserving method IDs and required-response fingerprints.
- Protocol v2 Error writers and readers reject the reserved `SharpLinkErrorCode.Unknown` value; concrete defined wire codes retain their existing values and round trips.

### Compatibility and validation

- Valid Protocol v2 bytes, method IDs, payload layouts, and required-response fingerprints are unchanged. Existing two-argument `PooledAsyncStreamDispatcher<T>.Rent` binaries remain compatible; generated callers use new nullability-aware overloads.
- Pre-fix Generator preserved all 119 existing passes and failed only the new separate-compilation fingerprint proof; Unit preserved all 486 existing passes and failed exactly four new scalar-null, two-direction stream-null, and reserved-code proofs.
- Non-incremental Release built with zero warnings/errors; Generator 120/120, Unit 490/490, Integration 250/250, 120-second shared-memory Chaos, NativeAOT TCP, seven-package pack, and fresh-cache TCP/shared-memory package smoke passed. Exact-0.8.40/candidate TCP process medians changed by +0.36% without interceptors and +0.98% with interceptors, with unchanged allocations; required-reference stream dispatch was exactly 13.860 ns/op on both process medians.

## [0.8.40] - 2026-07-27

### Fixed

- Generated Stubs now classify absent invocation categories as structured `Unimplemented`; the obsolete public `RpcException` type has been removed.
- `SharpLinkException` rejects `Unknown` and undefined error codes at construction, so invalid custom mapper output falls through the Server's safe `Internal` boundary instead of breaking Protocol v2 error serialization.
- Client and Server interceptor pipelines join an invoked incomplete continuation even when interceptor code discards its `ValueTask`, preventing orphaned terminal calls and response-buffer lifetime races.
- Generated Proxy/Stub signatures preserve nullable reference annotations, and non-nullable scalar/stream responses reject null at generated service and Client short-circuit boundaries while nullable responses remain valid.

### Compatibility and validation

- Valid Protocol v2 framing, route hashes, request schemas, manifest wire types, and payload layouts are unchanged. Generated method metadata adds response nullability; its Boolean flags are packed into a 40-byte descriptor while retaining both the legacy nine-value and new ten-value deconstruction shapes.
- Pre-fix Generator preserved 118 existing passes and failed only the new empty-category proof; targeted Abstractions preserved 21 existing passes and failed exactly two new code/public-surface proofs; the Interceptor Integration class preserved 14 existing passes and failed exactly four new join/nullability/mapper proofs.
- Non-incremental Release built with zero warnings/errors; Generator 119/119, Unit 486/486, Integration 250/250, 120-second shared-memory Chaos, NativeAOT TCP, seven-package pack, and fresh-cache TCP/shared-memory package smoke passed. Exact-0.8.39/candidate intercepted-RPC process medians were 39.845 -> 40.234 microseconds (+0.98%, overlapping ranges), while allocation fell from approximately 1,584 to 1,560 B/op.

## [0.8.39] - 2026-07-27

### Fixed

- Server terminal failures now populate interceptor context status, error code, exception, and elapsed time before unwinding through interceptor code.
- Response-bearing Server interceptors that return without invoking their continuation fail locally as a structured `Internal` error instead of emitting an empty successful response.
- Client interceptor short-circuit results are validated inside the tracked pipeline, so wrong unary, streaming, or OneWay result shapes record `Failed` before reaching the caller.
- Framework consumption of application client streams no longer captures a caller `SynchronizationContext` at each asynchronous `MoveNextAsync`.
- Generated request Codecs, generated Server Stub decoders, and `RpcEmptyRequestCodec` classify malformed peer-controlled wire input as structured `DataLoss` instead of unstructured `InvalidDataException`/`Internal`.

### Compatibility and validation

- Valid Protocol v2 bytes, route hashes, payload layouts, interceptor ordering, and the zero-interceptor fast path are unchanged. OneWay Server interceptors retain their no-response short-circuit behavior, and arbitrary application `InvalidDataException` remains `Internal`.
- Pre-fix Generator preserved 117 existing passes and failed only the new generated-wire proof; Unit failed only the new empty-request proof; Integration preserved 9 existing interceptor passes and failed exactly four new context/continuation/type/synchronization proofs.
- Non-incremental Release built with zero warnings/errors; Generator 118/118, Unit 484/484, Integration 246/246, 120-second shared-memory Chaos, NativeAOT TCP, seven-package pack, and fresh-cache TCP/shared-memory package smoke passed. Exact-0.8.38/candidate intercepted-RPC process medians were 41.267 -> 40.831 microseconds (-1.06%) with unchanged approximately 1,584.02-1,584.05 B/op.

## [0.8.38] - 2026-07-27

### Fixed

- Service constructors whose dependencies cannot be supplied through the generated `IServiceProvider` activator now report `SHARPLINK019` instead of leaking generated `CS1620`, `CS0030`, `CS9193`, or unsafe/generic-shape failures.
- Native DTO construction accounts for the C# required-member contract, including ignored required members, required fields, and `SetsRequiredMembersAttribute`, replacing generated `CS9035` with `SHARPLINK012` only when no compiler-valid plan exists.
- DTO constructors requiring `ref`, `out`, or `ref readonly` storage are excluded from construction-plan selection; another valid value constructor may still be selected, otherwise `SHARPLINK012` replaces generated errors/warnings.
- Pointer and function-pointer payloads report `SHARPLINK009` before the unmanaged fast path and suppress Proxy/Stub artifacts that cannot represent those values.
- Client and Server interceptor contexts classify a structured `SharpLinkException` with code `Cancelled` as `SharpLinkInvocationStatus.Cancelled` instead of the contradictory `Failed` status.

### Compatibility and validation

- Valid Protocol v2 payloads, route hashes, service activators, and DTO Codecs are unchanged. `in` constructor dependencies, `SetsRequiredMembers` constructors, and fallback value constructors remain supported.
- Real pre-fix projects produced service `CS1620`/`CS0030`/`CS9193`, DTO `CS9035`/`CS1620`, and ten pointer Proxy `CS0214`/`CS0306` errors. Post-fix they expose focused `SHARPLINK019`, `SHARPLINK012`, and `SHARPLINK009` diagnostics; the positive-control project builds cleanly.
- Non-incremental Release build, Generator 117/117, Unit 483/483, Integration 241/241, NativeAOT, and 120-second shared-memory Chaos passed. Exact-baseline/candidate HostApplication build medians were 1.97 -> 1.92 seconds; intercepted RPC medians were 41.848 -> 41.831 microseconds with unchanged 1,584.03 B/op.

## [0.8.37] - 2026-07-27

### Fixed

- Generator analysis reports `SHARPLINK018`/`SHARPLINK009` when service or explicit DTO declarations cannot be reached from the sibling generated namespace, replacing raw generated C# accessibility failures.
- Generated DTO Codecs keep escaped keyword syntax only on member access and compose locals from raw symbol names, so members such as `@class` produce valid C#.
- Native generated DTO Codecs reject unsealed record classes instead of silently slicing derived record state through a base record schema.
- Ref-like DTO payloads report `SHARPLINK009` and suppress contract artifacts that cannot legally store or use them as ordinary generic arguments.
- RPC contracts with static abstract operators or conversions report `SHARPLINK054` and suppress an unimplementable Proxy.
- The admission/drain race gate now models the production `Interlocked.Exchange` state transition instead of a weaker volatile store that could create a false ARM64 store-buffering witness under process-level load.

### Compatibility and validation

- Protocol v2, route hashes, and valid generated payloads are unchanged. Internal and protected-internal same-assembly service/DTO declarations remain supported.
- Unsealed record DTOs must be sealed or routed through an explicit Codec Adapter. Ref-like RPC payloads and static abstract operator/conversion contracts were never valid generated artifacts and now fail with SharpLink diagnostics.
- Pre-fix Generator preserved all 108 existing passes and failed exactly five new probes; post-fix Generator is 113/113. Non-incremental Release build, Unit 483/483, and Integration 240/240 passed. The corrected race gate also passed three consecutive full Unit reruns after its load-only false positive was reproduced.
- Interleaved exact-0.8.36/current non-incremental HostApplication builds measured median wall time 2.13 -> 1.89 seconds; the batch changes no runtime hot-path IL.

## [0.8.36] - 2026-07-27

### Fixed

- Server call admission publishes the global active count before its final Running-state check, so Stop cannot observe a completed drain and then admit a racing request; rejected races roll back both counters.
- Server Stop now joins connection-scoped asynchronous service cleanup for connections with no remaining calls, while preserving bounded deferred cleanup for explicitly uncooperative calls.
- Performance-profile queue defaults apply only when `MaxSendQueueBytes` was never assigned, preserving an explicit 8 MiB value under LowLatency or Throughput.
- Protocol v2 handshake response writers and readers reject compression capability/profile mismatches at the public codec boundary.

### Changed

- Removed the unusable `SharpLinkCallOptions.EnableCompression` member. Compression remains automatic after Client/Server provider negotiation and continues to obey runtime payload and savings thresholds.

### Compatibility and validation

- Valid Protocol v2 payloads and route hashes are unchanged. Code that initialized `EnableCompression` must remove that initializer and configure providers through `UseRuntime`; invalid handshake responses now fail at their codec trust boundary.
- Pre-fix Unit preserved all 479 existing passes and failed only four new probes; Integration preserved all 239 existing passes and failed only the new cleanup-join probe.
- The exact 0.8.35 admission/release hot-path baseline and 0.8.36 candidate both allocate zero. Median-of-process medians measured 5.1399 -> 5.1706 ns (+0.60%), within the 5% no-regression gate.
- Non-incremental Release build, Generator 108/108, Unit 483/483, Integration 240/240, 120-second shared-memory Chaos, NativeAOT, and seven-package pre-commit pack passed.

## [0.8.35] - 2026-07-27

### Fixed

- Dynamic endpoint resolver failures owned by the retry state machine now use dedicated Warning event `6102` instead of unhandled-background Error `6002`; cleanup failures remain Errors.
- Client and Server loops classify ordinary transport/session termination during rolling restart as expected disconnects without hiding protocol or internal failures.
- Client and Server protocol termination releases the active `PipeReader` result before connection disposal can join reader completion, preventing teardown self-deadlock.
- The Chaos release gate captures bounded Server as well as Client Error evidence, exposes an injected Server-error self-test, and fails when either side emits an Error.
- An explicitly requested Chaos JSON report that cannot be written now produces exit code 6 instead of a false successful release result.
- Internal runtime profile reads use the frozen context snapshot directly instead of deep-cloning public options during Client Build, Server Build, and session send-pump creation.

### Compatibility and validation

- Protocol v2, route hashes, and valid payloads are unchanged. `LogEvents.Client.ResolverUpdateFailed` is an additive event ID; `ChaosReport.ServerErrors` is an additive diagnostic field.
- Non-incremental Release build, Generator, Unit, Integration, shared-memory/TCP Chaos, NativeAOT, seven-package pack, and fresh-cache package smoke passed.
- Client Build allocation improved from 6,536 to 6,168 B/op (−368 B, −5.6%); all three candidate latency medians were below the three exact-baseline medians.

## [0.8.34] - 2026-07-27

### Fixed

- Shared-memory reader completion now waits for both an in-progress read operation and any returned `ReadResult` before releasing staged bytes or mapping ownership, closing a teardown race that produced `NullReferenceException` under restart injection.
- The Chaos release gate retains bounded aggregate client Error evidence across server generations and fails when any Error is captured; its opt-in injected-error self-test verifies the process exit and report.
- Inherited RPC declarations with the same CLR signature now report `SHARPLINK057` when Oneway shape, timeout/idempotency/cancellation policy, serialized parameter names, or nested nullability disagree; an explicit derived redeclaration remains the canonical opt-in resolution.
- Client and Server request loops accept the terminal `StreamPipeReader.AdvanceTo` race only after session close or cancellation, while preserving non-terminal invalid-buffer failures.
- Recoverable fixed, static-cluster, and dynamic-cluster expansion/reconnect failures now use Warning event `6101` instead of the unhandled-background Error event `6002`.
- The bounded dispatcher-pool collectability test no longer relies on async-state-machine/JIT temporary lifetime and now provides deterministic weak-reference evidence.

### Compatibility and validation

- Protocol v2, route hashes, valid payloads, and generated output for unambiguous contracts are unchanged. `LogEvents.Client.ConnectionAttemptFailed` is an additive public event ID; handled connection-attempt failures move from Error `6002` to Warning `6101`.
- Non-incremental Release build, Generator 108/108, Unit 478/478, Integration 238/238, shared-memory Chaos, NativeAOT, seven-package pack, and fresh-cache package smoke passed.
- Shared-memory reader A/B measured 29.564 -> 30.046 ns with unchanged 40 B/sample (+1.63%). Alternating inherited-Generator runs improved the median of process medians from 17.469 to 16.652 ms and allocation from 30,720,156 to 30,654,364 B.

## [0.8.33] - 2026-07-27

### Fixed

- Inherited RPC methods with identical parameter signatures but incompatible return types now report `SHARPLINK057` and suppress broken Proxy/Stub output instead of silently collapsing one declaration.
- Generated Stub size fields include a deterministic type-identity suffix, preventing distinct enum names that sanitize to the same C# identifier from producing duplicate fields.
- Synchronous Client and Server Builder rollback now runs asynchronous resource cleanup away from the caller's synchronization context, preserving completion and aggregated failures without deadlocking a non-pumping context.
- Duplicate Client Hosted Service Start is rejected without disposing or losing the already-owned client and without poisoning its accessor.
- Duplicate Multi-Cluster Hosted Service Start independently preserves the existing coordinator and accessor instead of transferring them into startup-failure cleanup.

### Compatibility and validation

- Protocol v2, route hashes, valid generated contracts, and public API are unchanged. `SHARPLINK057` rejects an interface shape for which a generated class cannot implement both inherited declarations.
- Non-incremental Release build, Generator 104/104, Unit 477/477, Integration 238/238, seven-package pack, and fresh-cache package smoke passed.
- A 40-contract/400-enum-method Generator stress gate measured 20.192 ms / 32,888,392 B at the 0.8.32 baseline and 15.116 ms / 33,142,168 B for 0.8.33. Latency did not regress; the 0.77% allocation increase is the bounded cost of collision-resistant generated identifiers in this deliberately enum-heavy fixture.

## [0.8.32] - 2026-07-27

### Fixed

- Unix-domain listener cleanup now preserves an existing path entry when bind succeeded but socket identity capture did not, avoiding deletion of a caller replacement in that narrow failure window.
- Compression negotiation freezes every validated wire-profile/provider binding at Runtime Context Build, so later mutation of a custom provider's `WireProfile` cannot change advertisement, selection, lookup, or diagnostics.
- Authentication result factories reject undefined error codes, while the Server trust boundary normalizes a provider-created undefined rejection to `AuthenticationRejected` instead of faulting handshake encoding.
- Any positive default request timeout is usable: deadlines beyond the `DateTimeOffset` range saturate at `DateTimeOffset.MaxValue` for ordinary calls and health checks.
- Immediate server admission uses exact limiter slots and a single-lease fast path instead of allocating three oversized/transient arrays for the common concurrency-only rule.

### Compatibility and validation

- Protocol v2 framing and valid payloads are unchanged. Compression still uses the original provider instance, but the profile validated at Build is its immutable wire identity for that Runtime Context.
- Non-incremental Release build, Generator 102/102, Unit 474/474, Integration 238/238, seven-package pack, and fresh-cache package smoke passed.
- Immediate admission improved from 58.477 ns / 568 B to 49.262 ns / 288 B. A pooled-array candidate measured 93.996 ns / 232 B and was rejected rather than trading allocation for latency.

## [0.8.31] - 2026-07-27

### Fixed

- Socket client factories now snapshot supported custom mutable `EndPoint` implementations through `Create(Serialize())` and reject implementations that cannot produce an independent snapshot.
- Unix-domain listener disposal identifies its own filesystem socket by file type, device, and inode, preserving a caller-owned entry that replaced the bound path.
- Anonymous-pipe offers redact inheritable handles from diagnostics and expose idempotent parent-side transfer completion so servers can observe a child process closing its handles.

### Changed

- The duplicate raw `ProtocolV2FrameWriter`/token surface, packet writer helpers, and striped runtime map are now implementation details. The unsupported `ISerializer`, `IServiceRegister`, `StripedLongSet`, `GeneratedProxyRegistry`, and `GeneratedStubRegistry` surfaces were removed.

### Compatibility and validation

- Protocol v2 wire framing and generated RPC paths are unchanged. Consumers of removed/internalized implementation APIs must migrate to generated proxies/stubs, `IRpcCodec`/`IRpcCodecAdapter`, builders, and framework-owned runtime state.
- Non-incremental Release build, Generator 102/102, Unit 470/470, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- The restored raw frame body measured 3.473 ns at the contemporaneous 0.8.30 baseline and 3.524 ns for 0.8.31 (about +1.5%, inside the 5% nanosecond-scale gate), with 0 B/op for both; the production method body is unchanged.

## [0.8.30] - 2026-07-27

### Fixed

- Generic Host server shutdown no longer treats an expected faulted Run completion as an unexpected application-fatal failure after Stop has begun.
- A completed hosted server Stop is now terminal: later Start attempts are rejected instead of publishing a server that the cached Stop task cannot own; duplicate Start is also rejected.
- Source generation now models the outer Task/ValueTask shape exactly, so valid `Task<T>` methods whose response type name contains `ValueTask` emit Task-compatible Proxy and Stub code.
- Public named-pipe and shared-memory endpoint address values reject NUL and path separators at construction, matching their concrete transport factories.
- Local server health checks reuse three immutable completed results instead of allocating a 96-byte Task on every poll.

### Compatibility and validation

- Protocol v2, valid generated contract shapes, payloads, and default hosting behavior are unchanged. Post-stop hosted restart was never owned by the lifecycle contract; invalid logical pipe names now fail at their earliest public value constructor.
- Non-incremental Release build, Generator 102/102, Unit 468/468, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- A 40-contract/400-method Generator gate measured 15.438 → 15.411 ms with effectively unchanged compiler-thread allocation. Local health polling changed from 96 B/call to 0 B/call; its 15-sample latency distribution was bimodal but overlapping, with a bounded roughly 5 ns worst median difference in a once-per-health-poll path.

## [0.8.29] - 2026-07-27

### Fixed

- Pending requests that race table disposal are now completed instead of being inserted after the disposal scan; stream registration APIs reject calls begun after disposal consistently with unary registration.
- Client and server heartbeat expiry now uses monotonic elapsed time, so wall-clock adjustments and caller-written `LastActive` values cannot suppress or spuriously trigger disconnects.
- Named-pipe and shared-memory constructors now reject logical names containing NUL or path separators during configuration on every platform.
- Unix-domain endpoint snapshots now preserve serialized abstract-namespace addresses, and abstract sockets are excluded from filesystem ownership and cleanup.
- Multi-cluster Ready/Degraded state reads no longer allocate a LINQ iterator on every observation.

### Compatibility and validation

- Protocol v2 framing, payloads, and generated code are unchanged. `IRpcSession.LastActive` remains a wall-clock diagnostic property, but framework heartbeat decisions no longer use caller-written values.
- Non-incremental Release build, Generator 101/101, Unit 464/464, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- Alternating 15-sample A/B kept pending response completion at 37.176 → 37.127 ns with unchanged 24 B/op. Multi-cluster state improved from 8.972 ns / 56 B to 3.189 ns / 0 B; monotonic activity update/check adds about 4.04 ns with 0 B/op and does not affect the request-table, serialization, or send paths.

## [0.8.28] - 2026-07-27

### Fixed

- TCP keep-alive time and interval now reject values beyond the native signed integer-seconds range during configuration instead of overflowing when a socket is created.
- Token-bucket, fixed-window, and sliding-window admission periods now reject values beyond the portable timer range before constructing a runtime limiter.
- Named-pipe client and server constructors now reject undefined option bits and transmission modes, and clients reject the server-only `FirstPipeInstance` bit, instead of deferring unusable configuration until connect or accept.
- Sliding-window admission now rejects configurations whose segment duration would round down to zero `TimeSpan` ticks.
- Binary protocol error writers now reject undefined `SharpLinkErrorCode` values before writing a payload that the matching reader cannot accept.

### Compatibility and validation

- Protocol v2 framing and every valid payload layout are unchanged. Only undefined error-code writes and previously unusable transport/admission configurations are rejected earlier.
- Non-incremental Release build, Generator 101/101, Unit 459/459, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- Fifteen-sample boundary A/B kept binary error writing at 11.888 → 11.968 ns with 0 B/op. Configuration validation added 1.14 ns to socket option freezing and 1.49 ns across three admission policies; alternating runtime A/B remained within a 5% no-regression gate with unchanged allocations.

## [0.8.27] - 2026-07-27

### Fixed

- Payload-bearing responses now always pass empty input to their registered Codec instead of silently returning `default(T)`; payload-less acknowledgements reject unexpected bytes as `DataLoss`.
- A response stream now preserves both its call/lease cancellation token and a distinct consumer enumeration token instead of allowing the latter to mask the former.
- Concurrent writer Return and pool Dispose can no longer enqueue an ArrayPool-backed writer into a detached queue after disposal.
- An unexpectedly successful hosted Server run-loop exit now logs a critical event and stops the owning Host; an explicit hosted stop remains quiet.
- Anonymous-pipe client offers remain one-shot after a failed connection attempt and can no longer retry handles that may already have been consumed or closed.

### Compatibility and validation

- Protocol v2 framing and valid payload layouts are unchanged. Peers that incorrectly omit a required response payload or attach bytes to a payload-less acknowledgement now receive `DataLoss` instead of a silent default/acceptance.
- Non-incremental Release build, Generator 101/101, Unit 454/454, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- Fifteen-sample A/B medians measured writer rent/return at 8.884 → 8.830 ns, Int32 response completion at 44.174 → 43.836 ns, and stream dispatch/consume at 16.795 → 16.803 ns; allocations were unchanged.

## [0.8.26] - 2026-07-27

### Fixed

- `[Oneway]` RPCs must now return non-generic `Task` or `ValueTask`; response-bearing and streaming shapes report `SHARPLINK056` instead of generating calls that cannot honor fire-and-forget semantics.
- Proxy request and stream locals now avoid every user-parameter collision, including chained underscore variants.
- DTO members that differ only by case no longer crash constructor analysis; exact constructor-parameter matches take priority, while ambiguous case-insensitive fallback reports the existing `SHARPLINK012` diagnostic.
- Generated dictionary readers now translate null keys to an RPC `DataLoss` error before reaching `Dictionary.TryAdd`; duplicate keys keep the existing `DataLoss` behavior.
- Non-public default interface helpers are no longer emitted as RPC routes, while non-public abstract methods report `SHARPLINK054`.

### Compatibility and validation

- Protocol v2, route hashes, payload layouts, Manifest schema, and valid public RPC surfaces are unchanged. Previously generated invalid Oneway and non-public abstract surfaces had no usable RPC contract.
- Non-incremental Release build, Generator 101/101, Unit 449/449, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- A 40-contract/400-method, 101-sample Generator A/B measured 14.755 → 13.530 ms. Compiler-thread allocation increased by 76,640 B (0.27%); an isolated 16-key dictionary guard comparison measured 171.891 → 170.941 ns.

## [0.8.25] - 2026-07-27

### Fixed

- Collision-resistant Roslyn hint names prevent distinct fully-qualified contracts from crashing generation with CS8785; public nested contracts now receive deterministic unique Proxy/Stub/helper type names.
- C# keyword RPC method and parameter names now remain escaped in every generated declaration and reference without changing contract hashes or Manifest identities.
- `ref`, `out`, `in`, and by-ref return signatures now report `SHARPLINK052` instead of producing unusable generated implementations.
- Static RPC methods now report `SHARPLINK053` instead of being modeled as instance routes.
- Abstract contract properties, indexers, and events now report `SHARPLINK054` instead of leaving generated proxies incomplete.

### Changed

- Contracts and every containing type must be public (`SHARPLINK055`); nested contracts inside generic containing types reuse `SHARPLINK005`. Default interface properties/events with implementations remain allowed and are not RPC routes.

### Compatibility and validation

- Protocol v2, route hashes, payload layouts, top-level Proxy/Stub type names, and Manifest schema are unchanged. Generated type names for nested contracts now include containing-type identity; Roslyn hint names are build-internal and now include the existing contract ID.
- Non-incremental Release build, Generator 96/96, Unit 449/449, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- A 40-contract/400-method, 101-sample Generator A/B measured 15.953 → 13.577 ms with overlapping quartiles. Compiler-thread allocation increased 40,976 B (0.14%); runtime hot paths are unchanged.

## [0.8.24] - 2026-07-27

### Fixed

- Invalid `[Timeout]` constants now report `SHARPLINK050` instead of emitting uncompilable or type-initializer-failing RPC descriptors.
- Union tags must now be positive, and union cases must be closed, concrete, assignable, and mapped to exactly one tag; invalid declarations report `SHARPLINK051`.
- An explicit empty `[assembly: SharpLinkRpcContracts()]` filter now disables referenced-contract discovery instead of falling back to automatic scanning.
- Generated assembly and JSON contract Manifests now report the executing generator package version instead of the stale hard-coded `0.8.3` value.

### Changed

- Removed a redundant constant-false branch from RPC method validation while folding timeout checks into the existing traversal.

### Compatibility and validation

- Protocol v2 and payload layouts are unchanged. Contract `schemaFingerprint` values change because corrected generator provenance is part of the integrity-protected JSON; baseline compatibility comparison remains structural.
- Non-incremental Release build, Generator 88/88, Unit 449/449, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- A duplicate method-analysis pipeline that regressed a 400-method synthetic generator workload by about 20% was rejected. The final 101-sample A/B measured 41.029 ms at the 0.8.23 baseline and 40.675 ms for 0.8.24; compiler-thread allocation moved by a bounded 0.57%, while runtime hot paths are unchanged.

## [0.8.23] - 2026-07-27

### Fixed

- Boolean blit collections now reject non-canonical element bytes across array, List, Memory, ReadOnlyMemory, and ImmutableArray Codecs.
- Rune and decimal blit collections now apply the same semantic validation as their scalar Codecs.
- DateOnly, DateTime, and TimeOnly blit collections now reject invalid temporal values.
- DateTimeOffset blit collections now reject invalid UTC ticks or offsets and clear native padding without mutating caller-owned values.
- Truncated shared-memory server responses now surface `Unavailable` from Client Connect while preserving the original EOF as the inner cause.

### Compatibility and validation

- Collection counts, element layouts, and Protocol v2 framing are unchanged. Valid older payloads remain readable; new DateTimeOffset collection writers canonicalize padding to zero.
- Non-incremental Release build, Generator 84/84, Unit 449/449, Integration 237/237, seven-package pack, and fresh-cache package smoke passed.
- All-bit-pattern-valid `int[]` serialize/deserialize retained about 10.1/17.0 ns and 0/88 B/op. Sixteen-element Boolean and DateTimeOffset validation add about 5 ns and 23 ns respectively with unchanged allocations; two shared-helper designs that regressed ordinary writes were rejected.

## [0.8.22] - 2026-07-27

### Fixed

- Generated DTO Boolean fields now reject non-canonical payload bytes instead of materializing invalid Boolean bit patterns.
- Generated DTO Rune fields now reject values outside the Unicode scalar range.
- Generated DTO decimal fields now reject invalid flags layouts.
- Generated DTO DateOnly, DateTime, and TimeOnly fields now reject values outside their supported ranges.
- Generated DTO DateTimeOffset fields now validate UTC ticks and offsets and clear native-layout padding before transmission.

### Compatibility and validation

- Protocol v2 framing, generated field IDs, fixed wire types, payload sizes, and Manifest versions are unchanged. New readers accept valid prior payloads, while new DateTimeOffset writers canonicalize six padding bytes to zero.
- Non-incremental Release build, Generator 84/84, Unit 445/445, Integration 236/236, seven-package pack, and fresh-cache package smoke passed.
- Boolean and semantic DTO paths retained their allocation profiles. The final fixed-wire validation adds only about 1–2 ns to a six-field semantic decode; a length-delimited Codec design measured 66/109 ns for serialize/deserialize and was rejected.

## [0.8.21] - 2026-07-27

### Fixed

- Shared-memory handshakes now reject malformed UTF-8 mapping paths before filesystem security validation.
- Generated null collection payloads now reject trailing bytes consistently with non-null collections.
- Generated DTO string serialization now rejects isolated UTF-16 surrogates instead of replacement-encoding them.
- Request metadata sizing and encoding now reject isolated surrogates instead of changing keys or values on the wire.
- Dynamic per-call service scope-creation failure now releases its module lease, preventing plugin drains from being stranded.

### Changed

- Removed two unused internal writer/nullable-short serialization helpers.

### Compatibility and validation

- Protocol v2 wire formats and generated Manifest versions are unchanged. Previously normalized malformed local or peer text is now rejected; valid Unicode including surrogate pairs is unchanged.
- Non-incremental Release build, Generator 83/83, Unit 445/445, Integration 231/231, seven-package pack, and fresh-cache package smoke passed.
- Metadata construction retained 136 B/op and baseline latency. Strict sizing adds about 2 ns; strict generated string writes add about 4 ns with zero allocation, an intentionally bounded integrity cost after a slower extra-scan design was rejected.

## [0.8.20] - 2026-07-27

### Fixed

- RPC, TLS, and shared-memory handshake timeouts now reject values beyond the portable native timer range during configuration, before connection or transport ownership is acquired.
- Far-future Client readiness deadlines now remain cancellable instead of failing immediately in `Task.WaitAsync`.
- Far-future pending-request admission deadlines now remain cancellable instead of failing immediately in `SemaphoreSlim.WaitAsync`.
- Server graceful Stop now slices timer-range-exceeding waits, preventing a saturated monotonic deadline from forcing an immediate stop.
- Generated DTO string codecs now reject malformed UTF-8 as `DataLoss` instead of silently replacing invalid bytes with U+FFFD.

### Compatibility and validation

- Protocol v2 framing and generated Manifest versions are unchanged. RPC, TLS, and shared-memory handshake timeouts are now limited to 2,147,483,647 ms; other far-future deadlines remain supported through cancellable timer slices. Malformed generated string fields that were previously normalized are now rejected.
- Non-incremental Release build, Generator 83/83, Unit 441/441, Integration 230/230, seven-package pack, and fresh-cache package smoke passed.
- Valid contiguous generated-string decoding retained 64 B/op with overlapping baseline/candidate latency. Segmented decoding retained 112 B/op and adds about 3.5 ns (roughly 3%) for replacement-marker detection; an always-strict decoder and a separate full byte-validation pass were rejected after measuring roughly 8% and 10% regressions.

## [0.8.19] - 2026-07-27

### Fixed

- Server authentication now rejects contradictory provider results that claim success while carrying a concrete rejection or failure code.
- Client and Server interceptors now receive single-use continuations, preventing duplicate or concurrent `next` calls from executing a non-idempotent RPC or service method more than once.
- Faulted Client background tasks are now observed and logged after leaving the tracking set instead of silently disappearing before Stop can inspect them.
- Generic Host Server Stop now preserves caller cancellation or Stop failure together with later readiness, token-owner, and Server disposal failures.
- Endpoint polling and Client/Server heartbeat delays now slice timer-range-exceeding intervals; Server admission rejects queue delays beyond the portable timer range during configuration.

### Compatibility and validation

- Protocol v2 wire formats and generated Manifest versions are unchanged. Interceptor `next` delegates are now single-use and throw `InvalidOperationException` on a second call. Contradictory authenticated provider results are rejected, `MaxQueueDelay` above 2,147,483,647 ms is invalid, and Hosted Server Stop may return `AggregateException` when primary and cleanup failures differ.
- Non-incremental Release build, Generator 83/83, Unit 436/436, Integration 230/230, seven-package pack, and fresh-cache package smoke passed.
- The no-interceptor RPC path retained 320 B/op with overlapping latency ranges. One Client plus one Server interceptor intentionally adds 32 B per end-to-end call for two single-use continuation guards, while median latency remained in the same roughly 40–41 µs band.

## [0.8.18] - 2026-07-27

### Fixed

- Hosted single- and multi-cluster Clients now remain owned through cancellation or failure of token-bound Stop and are always disposed, with Stop and disposal failures preserved together.
- Dynamic Client and Server assembly drains now slice timer-range-exceeding graceful timeouts instead of faulting after the module has entered Draining.
- Timed send batching now saturates monotonic deadline conversion and slices native timer waits, so huge positive flush latencies cannot become immediate flushes or pump faults.
- Server active-call concurrency now has a 1,048,576-per-connection hard maximum enforced by both public flow-control validation and the deadline scheduler.
- Terminal stream cleanup now detaches every dispatcher outside the request lock before surfacing completion failures; RpcSession terminal cleanup cannot be interrupted by a user dispatcher.

### Compatibility and validation

- Protocol v2 wire formats and generated Manifest versions are unchanged. Hosted Client Stop now also calls `DisposeAsync` on its transferred owner. Direct `StreamManager.CompleteAll` still surfaces completion failures after all entries are drained, while RpcSession suppresses them to finish transport cleanup. `MaxConcurrentCallsPerConnection` values above 1,048,576 are rejected.
- Non-incremental Release build, Generator 83/83, Unit 432/432, Integration 228/228, seven-package pack, and fresh-cache package smoke passed.
- Buffer-pool, pending, and flow-control hot-path allocations remained 0/48/0 B per operation with no stable latency regression. Robust two-stream terminal draining intentionally adds one 32 B shutdown snapshot; empty Session, Runtime Context, and Server lifecycle allocations are unchanged.

## [0.8.17] - 2026-07-27

### Fixed

- Concurrent multi-cluster assembly unregister callers now share one coordinator operation and preserve the original child rejection instead of racing route restoration.
- Client and Server TLS snapshots now deep-clone certificate chain policies; Server snapshots also preserve supported RSA signature-padding settings.
- Protocol v2 handshakes now reject required capabilities that were not advertised as supported and reject unknown negotiated response bits.
- Partitioned admission control now owns a deep validated snapshot of its limits instead of retaining caller-mutable configuration.
- Runtime state stores and retained writer pools now enforce hard aggregate sizing bounds before allocating or retaining memory.

### Compatibility and validation

- Protocol v2 wire formats and generated Manifest versions are unchanged. Unknown request capabilities remain forward-compatible and are handled by negotiation; malformed required/supported sets and unknown negotiated responses are rejected. Configurations above 1,024 stripes, 1,048,576 aggregate initial map entries, or 64 MiB of configured retained writer memory are rejected.
- Non-incremental Release build, Generator 83/83, Unit 427/427, Integration 228/228, seven-package pack, and fresh-cache package smoke passed.
- Hot-path allocations were unchanged with no stable latency regression. TLS policy and admission configuration snapshots intentionally add 88 B and 72 B respectively at cold configuration/lifecycle boundaries.

## [0.8.16] - 2026-07-27

### Fixed

- Client deadlines beyond the portable native timer interval are now re-armed in bounded slices instead of throwing after the pending call has already occupied a slot.
- Runtime Context disposal now drains retained writer buffers, rejects later rents, and releases active writer arrays when they are returned after disposal.
- Server Stop and Run now preserve immediate listener, framework, and service cleanup failures instead of reporting a successful stop with only an unhealthy status.
- The Generic Host server no longer retains the transient `StartAsync` cancellation token as the lifetime token of its Run loop.
- Pending-request tables now enforce a 1,048,576-slot hard maximum in both public protocol validation and their internal constructor.

### Compatibility and validation

- Public protocol wire formats and generated Manifest versions are unchanged. `SharpLinkBufferWriterPool` now implements `IDisposable`; a pool owned by a disposed Runtime Context rejects new rents. Server Stop/Dispose may now throw one cleanup exception or an `AggregateException`, and pending capacities above 1,048,576 are rejected during configuration.
- Non-incremental Release build, Generator 83/83, Unit 422/422, Integration 228/228, package smoke, and reversed same-machine A/B passed.
- Buffer rent/return and a 32-byte packet remained allocation-free with no stable latency regression; pending completion retained 48 B/op, while Runtime Context and Server lifecycle allocations were unchanged.

## [0.8.15] - 2026-07-27

### Fixed

- Unix-domain socket listeners no longer delete a pre-existing filesystem entry; stale paths must be removed explicitly by their owner.
- Socket Client factories now snapshot mutable IP endpoints, including IPv6 scope, at construction.
- Built-in socket, TLS, and shared-memory endpoint delegates now freeze configuration when the delegate is created, so later caller mutations cannot split topology generations.
- Direct Client transports and endpoint resolvers are transferred out of a builder after one build instead of being owned by multiple Clients.
- Server listeners are transferred out of a builder after one build and are released during failed-build rollback while preserving every build and cleanup failure.

### Compatibility and validation

- Public signatures, Protocol v2, and generated Manifest versions are unchanged. Reusing a direct Client or Server builder now requires supplying a new transport/resolver; static endpoint builders remain reusable because they create fresh factories. A pre-existing Unix socket path is no longer removed automatically.
- Non-incremental Release build, Generator 83/83, Unit 417/417, Integration 228/228, package smoke, and reversed same-machine A/B passed.
- Unchanged flow-credit and pending-completion hot paths retained 0/48 B per operation with no latency regression. Safe configuration snapshots intentionally add about 104 B per known IP endpoint and 56 B once per built-in socket delegate.

## [0.8.14] - 2026-07-27

### Fixed

- Unix named-pipe normalization now budgets the complete native path in UTF-8 bytes and never cuts a surrogate pair, so non-ASCII logical names remain within the kernel path limit.
- Named-pipe listeners now reject server-instance limits outside `-1` or 1 through 254 during construction instead of failing when the server begins accepting.
- Throwing client-stream producer cancellation callbacks are reported without escaping terminal pending-call completion or stranding the operation and pooled call.
- Socket Client factories and `UseTcp` now reject remote port zero consistently with DNS and endpoint-address APIs.
- Flow-control waiters blocked only by their own stream credit no longer stall independent streams that still have both stream and connection credit; connection-credit contention remains FIFO.

### Compatibility and validation

- Public signatures, Protocol v2, and generated Manifest versions are unchanged. Invalid named-pipe instance counts and Client TCP port zero now fail during configuration, and eligible streams may progress around a stream-local blocked waiter.
- Non-incremental Release build, Generator 83/83, Unit 411/411, Integration 228/228, package smoke, and reversed same-machine A/B passed.
- Uncontended flow-credit round trips stayed at 21.6-22.1 ns and 0 B/op. Normal producer completion and short ASCII named-pipe normalization retained 48 B/op and 272 B/op with process-order noise but no stable latency regression.

## [0.8.13] - 2026-07-27

### Fixed

- Shared-memory control disposal now joins its writer loop after stream closure instead of returning with a live background task.
- Cancellation tokens now wake blocked shared-memory control waits without relying on an unrelated peer or local pulse.
- A rejected concurrent PipeReader read can no longer replace the active read's cancellation registration.
- A rejected concurrent PipeReader read can no longer clear the active read's peer-notification state and strand it after data arrives.
- PipeWriter completion now converges with an active spill flush before releasing the spill buffer or returning.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged; invalid concurrent PipeReader calls remain rejected but no longer alter the accepted read.
- Non-incremental Release build, Generator 83/83, Unit 404/404, Integration 228/228, package smoke, and reversed same-machine A/B passed.
- Available-data Reader read/advance and default-token control waits remained about 71–73 ns and 20 ns at zero allocation. Normal writer completion remained in the same band while allocation fell from 280 B to 256 B.

## [0.8.12] - 2026-07-27

### Fixed

- Direct Client transport profile-binding rollback now disposes the Client-owned transport and preserves binding, transport, and Runtime Context cleanup failures.
- Direct Client construction rollback now releases its transport when later logger or option construction fails.
- Dynamic endpoint Client construction now releases its Client-owned resolver when validation or construction fails.
- Server service-definition validation rollback now preserves the primary validation failure together with every Runtime Context cleanup failure.
- Server construction rollback now covers logger and constructor failures, releases all created registrations and internal owners, and retains every cleanup failure.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged; failed builder transactions may now surface `AggregateException` when an owned extension also fails during rollback.
- Non-incremental Release build, Generator 83/83, Unit 399/399, Integration 228/228, package smoke, and alternating same-machine A/B passed.
- Direct and dynamic Client Build/Dispose retained 6.37/7.38 KB and showed no stable sub-1% latency regression across reversed runs; Server retained or improved latency and allocation fell from 12.94 to 12.88 KB.

## [0.8.11] - 2026-07-27

### Fixed

- Client runtime registration rollback now preserves a structured rejection together with generated Codec Adapter cleanup failure.
- Server runtime registration rollback now preserves a structured rejection, cleans every candidate service, and retains generated Codec Adapter cleanup failure.
- Client runtime replacement rollback now preserves its structured preparation rejection together with generated Codec Adapter cleanup failure.
- Server runtime replacement rollback now preserves its structured preparation rejection while completing candidate-service and generated Codec cleanup.
- Server profile binding failure now disposes the newly built Runtime Context and preserves both binding and Context cleanup failures.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged; failed dynamic transactions may now surface `AggregateException` only when rollback also fails, with the transaction rejection first.
- Release build, Generator 83/83, Unit 394/394, Integration 228/228, package smoke, and same-machine alternating A/B passed.
- Normal Client registration/unregistration measured 6.535 → 6.518 µs with 30.50 → 30.44 KB; Server measured 6.407 → 6.407 µs across two reversed-order runs, with the repeated run at 29.52 KB on both revisions.

## [0.8.10] - 2026-07-27

### Fixed

- Fixed-endpoint Client build rollback now preserves the primary validation failure together with transport cleanup failure.
- Endpoint transport profile-binding rollback now preserves both binding and factory cleanup failures.
- Generated Manifest preparation now preserves its primary factory/Scope failure together with every candidate Scope rollback failure.
- Runtime Context construction now preserves a later Manifest failure together with every previously prepared Manifest cleanup failure.
- Client construction now preserves its original build failure together with Runtime Context cleanup failure.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged; failed extension-point construction may now surface `AggregateException` with the primary cause first.
- Release build, Generator 83/83, Unit 389/389, Integration 228/228, package smoke, and same-machine A/B passed.
- Normal Runtime Context build/disposal measured 346.1 → 343.7 ns with unchanged 3.9 KB allocation after moving rollback aggregation to a no-inline cold path.

## [0.8.9] - 2026-07-27

### Fixed

- Shared-memory control disposal now joins reader termination after an unexpected stream cleanup failure and preserves the terminal failure set.
- Concurrent single-client Hosted Stop/Dispose callers now await one shared client cleanup operation.
- Concurrent multi-cluster Hosted Stop/Dispose callers now await one shared coordinator cleanup operation.
- Anonymous-pipe, named-pipe, and shared-memory listeners now share asynchronous disposal completion instead of letting later callers return while pending resources drain.
- Anonymous-pipe listener cleanup now continues through every queued connection and disposes its cancellation owner after an earlier connection failure.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged; repeated lifecycle calls now observe the same completion or cleanup failure.
- Release build, Generator 83/83, Unit 384/384, Integration 228/228, package smoke, and same-machine A/B passed.
- Normal anonymous-pipe offer allocation/disposal remained at 2.576 → 2.597 µs with overlapping 99.9% confidence intervals and unchanged 2.13 KB allocation; an earlier 2.19 KB design was rejected.

## [0.8.8] - 2026-07-27

### Fixed

- Anonymous-pipe connection teardown now continues through reader and both owned pipe handles after an earlier pipeline or output cleanup failure.
- Shared-memory connection teardown now releases its mapping after control-channel cleanup fails and preserves failures from every cleanup stage.
- Dynamic-module release now reports every connection-service, registration, and generated Manifest cleanup failure after completing all owners.
- Server shutdown now preserves failures from every drained dynamic module instead of exposing only the first failed module.
- Server-wide service cleanup now preserves dynamic-module, static/provider, admission-controller, and Runtime Context failures together.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged; cleanup operations can now surface `AggregateException` when multiple owners fail.
- Release build, Generator 83/83, Unit 379/379, Integration 228/228, package smoke, and same-machine A/B passed.
- Normal anonymous-pipe offer allocation/disposal remained statistically flat at 2.590 → 2.592 µs with overlapping 99.9% confidence intervals and unchanged 2.13 KB allocation.

## [0.8.7] - 2026-07-27

### Fixed

- Concurrent ClientConnection disposal now joins the owned RpcSession teardown instead of returning before physical transport cleanup.
- Runtime Context and generated Adapter registration disposal now preserve every scope failure after completing all scopes.
- Concurrent Hosted Server stop callers now await one shared shutdown operation.
- Server connection close now preserves cancellation-callback and Session cleanup failures together.
- Throwing connection-cancellation callbacks are logged but can no longer strand pending RPC calls or skip stream completion.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged; multi-scope cleanup can now surface `AggregateException`.
- Release build, Generator 83/83, Unit 374/374, Integration 228/228, package smoke, and same-machine A/B passed.
- Normal ClientConnection disposal remained at 1.145 → 1.146 µs and 18.51 KB; two earlier allocating designs were rejected.

## [0.8.6] - 2026-07-27

### Fixed

- Stream transport and RPC session teardown now continue through every owned resource after unexpected completion failures and preserve the complete ordered error set.
- Concurrent RPC session disposers now observe the same terminal cleanup outcome.
- Connection-scoped and server-wide service cleanup now reports every disposal failure after completing all remaining services and the owned provider.
- Hosted servers now supervise asynchronous run-loop failure, log it, and request Generic Host shutdown instead of leaving a live process with a dead RPC endpoint.

### Compatibility and validation

- Public APIs, Protocol v2, and generated Manifest versions are unchanged. Cleanup callers may now receive `AggregateException` when multiple resources fail.
- Release build, Generator 83/83, Unit 369/369, Integration 228/228, package smoke, and same-machine disposal A/B passed.
- Normal session disposal measured 950.9 → 955.8 ns with overlapping 99.9% confidence intervals and unchanged 17.5 KB allocation.

## [0.8.5] - 2026-07-27

### Fixed

- Hosted single-client publication and terminal stop/failure are now serialized; a racing startup can no longer resurrect or return a client after the host stopped.
- Call- and connection-scoped activation rollback now preserves both the service-factory failure and scope cleanup failure.
- Call and connection service disposal now completes every cleanup layer and aggregates service and scope failures instead of silently discarding one cause.
- Fixed-client initial pool rollback now disposes every established connection, preserves the later connection failure together with all cleanup failures, and always leaves a failed attempt in `Faulted` rather than `Connecting`.
- Leased RPC invocation now preserves handler, request-stream completion, and lease cleanup failures together for exception mappers and diagnostics.

### Compatibility and validation

- Public APIs, generated Manifest versions, and Protocol v2 wire layouts are unchanged. Custom exception mappers may now receive an `AggregateException` when user execution and cleanup fail together; inspect its inner causes rather than assuming a single exception.
- Release build completed with 0 warnings/errors. Generator 83/83, Unit 364/364, Integration 228/228, five deterministic pre-fix failure probes, connection/call branch-completeness regressions, and the package restore/run smoke passed.
- Published-client accessor lookup remained allocation-free and statistically flat at 1.457 → 1.483 ns on the same Apple M4/.NET 10 benchmark gate; the 99.9% confidence intervals overlap.

## [0.8.4] - 2026-07-27

### Fixed

- Codec resolution now revalidates the generated-registration snapshot after user/native factory work, preventing a lookup that overlaps dynamic publication from returning or caching a superseded wire Codec.
- In-flight fallback and generated Codec resolution now observes Runtime Context disposal before publishing its result, so a disposed provider cannot be repopulated or return a newly resolved Codec.
- Pre-admission client-stream replay no longer synchronously waits for an incomplete dispatcher operation. Retained and newly arriving frames remain in one bounded ordered queue until the generated dispatcher is ready.
- Generated dispatcher configuration and replay now execute outside the per-request stream-registry lock, allowing safe callback reentrancy while preserving entry and buffer leases through asynchronous replay.
- Multi-cluster replacement now reconciles coordinator routes when the child committed its new assembly but old-generation cleanup failed; the original cleanup exception still reaches the caller.

### Compatibility and validation

- Public APIs and Protocol v2 wire layouts are unchanged. Rare Codec publication races may retry a fallback resolver or native generated factory, which must already tolerate concurrent resolution.
- Release build, Generator 83/83, Unit 357/357, Integration 228/228, six deterministic pre-fix failure probes across five findings plus branch-completeness regressions, and same-machine BenchmarkDotNet A/B validation passed.
- Cached explicit/fallback Codec lookup remained statistically flat at 6.529 → 6.533 ns and 6.515 → 6.504 ns. Cached generated lookup improved from 8.670 to 6.499 ns; attached pre-admission dispatch improved from 17.656 to 17.098 ns. All remained 0 B/op; an earlier 19.755 ns dispatch candidate was rejected and redesigned.

## [0.8.3] - 2026-07-26

### Fixed

- `SharpLinkEndpointSnapshot` now clones each endpoint and freezes nested attributes, so mutations through the source dictionary or a cast cannot alter a published topology.
- Fixed, static/dynamic-cluster, and multi-cluster client shutdown now await `CancelAsync`; blocking callbacks no longer prevent `StopAsync` from returning its asynchronous operation, and callback failures no longer skip remaining cleanup.
- Failed fixed/static/dynamic connection attempts now preserve both the primary connect/handshake exception and any transport/session cleanup failure instead of replacing the root cause.
- Client, multi-cluster, and Server HostedService startup cleanup now preserves the original startup/run failure together with cleanup errors and continues token cleanup.
- Protocol metadata decoding now adopts its already validated entry array instead of allocating and copying a second array.

### Compatibility and validation

- Valid wire payloads are unchanged. `SharpLinkMetadata` keeps its existing public constructor signature; only the Runtime receives internal validated-array ownership.
- Release build, Generator 83/83, Unit 348/348, Integration 227/227, four pre-fix mutation/lifecycle probes, and three-launch metadata benchmarks passed.
- Two-entry metadata decode improved from 68.33 ns / 280 B to 61.89 ns / 224 B. Public construction remained 80 B and showed no regression.

## [0.8.2] - 2026-07-26

### Fixed

- Fixed-endpoint `ConnectAsync` now owns its shared initialization independently of individual waiters, so cancellation by the first caller no longer cancels concurrent callers or faults the client-wide attempt.
- Fixed, static-cluster, and dynamic-cluster connections now share one handshake-timeout classifier. Endpoint clusters retain a structured `Unavailable` timeout cause instead of burying a linked-token `OperationCanceledException`.
- DNS last-good fallback now catches transient `SocketException` failures only. Unexpected resolver implementation failures propagate to callers or the supervised watch loop instead of being silently hidden forever.
- Protocol v2 length fields reject overlong VarUInt32 representations, restoring a single canonical wire encoding for metadata and error lengths.
- Binary error payloads are validated with strict UTF-8 across contiguous and segmented frames; malformed peer text now terminates the frame as `ProtocolViolation` instead of being lossily replaced.

### Compatibility and validation

- Valid Protocol v2 payloads and generated RPC layouts are unchanged. Peers that emitted overlong VarUInt32 values or invalid UTF-8 error text are now rejected and must emit the canonical writer format.
- Release build, Generator 83/83, Unit 344/344, Integration 227/227, five pre-fix failure probes, and three-launch frame-parser benchmarks passed.
- The metadata parser changed from 42.67 ns to 39.60 ns while the same-run control changed from 39.32 ns to 40.23 ns; both remained 0 B/op. Because host variance was high, the result is treated as a no-regression signal rather than an improvement claim.

## [0.8.1] - 2026-07-26

### Fixed

- Authentication scopes are now frozen snapshots. Callers can no longer cast `Scopes` to a mutable set, inject privileges, or contaminate the process-wide empty scope set.
- Endpoint snapshots and all generated assembly/cluster manifest collections now expose read-only wrappers, including nested method and service-dependency arrays.
- Built-in delegate and DNS endpoint resolvers now share idempotent asynchronous disposal, await cancellation callbacks, dispose their owned cancellation sources, and synchronize operation admission against disposal.
- Generated request decoders canonicalize and validate Boolean bytes and route semantic fixed values (`decimal`, date/time types, `Rune`, `Index`, and `Range`) through their validating built-in Codecs. Raw numeric hot paths remain inline.
- Native `List<T>` decoding now writes directly into List-owned storage, eliminating the intermediate array and full copy.

### Compatibility and validation

- Requests containing the semantic fixed values listed above use length-delimited Codec framing in 0.8.1. Rebuild and deploy affected Client/Server contracts together; Boolean and raw numeric request layouts remain unchanged.
- Release build, Generator 83/83, Unit 339/339, Integration 227/227, immutability/lifecycle mutation probes, and three-round alternating `Rpc_SumList` benchmarks passed.
- `Rpc_SumList` retained 99.56% throughput at 16 items and improved to 102.53% at 256 items. Allocations fell from 560 to 472 B/op and from 2480 to 1432 B/op.

## [0.8.0] - 2026-07-26

### Fixed

- Native built-in Codecs now reject truncated or trailing payload bytes consistently for contiguous and segmented input, and reject non-canonical Boolean and nullable-presence markers as `DataLoss`.
- Connection-level stream-credit batching now returns credit for every contributing stream instead of stranding credit on an idle open stream; the session emits each resulting `WindowUpdate` exactly once.
- RPC contracts now include inherited base-interface methods, including diagnostics, DTO-root discovery, proxy generation, stub dispatch, and deterministic handling of directly redeclared signatures.
- Unmanaged user-defined and nullable request parameters now use the selected registered Codec and length-delimited request framing instead of bypassing it through native layout blitting.

### Compatibility and validation

- Valid built-in Codec payloads remain compatible; previously accepted non-canonical or trailing bytes are now rejected.
- Generated request wire layout changes for nullable and user-defined unmanaged parameters, and inherited methods change the derived contract fingerprint. Rebuild and deploy both peers together; see `doc/migration-0.8.0.md`.
- Release build, Generator 81/81, Unit 336/336, Integration 227/227, targeted malformed-input coverage, emitted-frame verification, and same-machine BenchmarkDotNet comparison passed. The seven runtime hot paths retained allocations and ranged from 93.09% to 101.64% of baseline latency (lower is better), with no regression signal.

## [0.7.11] - 2026-07-26

### Added

- Added generic compile-time Codec Adapter registration and explicit binding APIs: `RpcCodecAdapterRegistrationAttribute`, `RpcCodecAdapterAttribute`, `IRpcCodecAdapter`, and `IRpcCodecAdapterScope`.
- Added `SharpLink.Serializer.SharpPack` with the exact SharpPack dependency range `[1.1.0]`. `[SharpPackable]` selects the Adapter automatically, while frameworks without a selector Attribute can use explicit type or assembly bindings.
- Contract manifests now require `wireFormatId` for every request, response, stream item, and DTO member, plus a top-level reachable Codec wire inventory. Compatibility compares wire identity rather than Adapter implementation identity, including Adapter types nested inside native collections.

### Changed

- Generated Manifest API is version 3. Adapter factories emit closed `CreateCodec<T>()` calls and contain no runtime generic construction, serializer scanning, or reflection resolver.
- Adapter state is owned per Runtime Context, Manifest instance, and Adapter ID. Automatic SharpPack Scopes own isolated formatter graphs. Dynamic register/replace/unregister publishes transactionally, validates every factory Adapter instance, and releases generation-owned Codec caches, Scopes, and serializer Contexts after drain even when another cleanup fails.
- SharpPack serialization uses a zero-allocation concrete writer bridge so SharpPack 1.1.0 remains visible to the NativeAOT IL scanner when SharpLink receives an `IBufferWriter<byte>` interface.
- Explicit `UseCodec` remains the highest priority and caller-owned. Ordinary supported DTOs continue to use SharpLink native generated Codecs even when an Adapter package is installed.

### Removed

- Removed `SharpLink.Serializer.MemoryPack`, the MemoryPack package dependency, `MemoryPackCodec`, `MemoryPackCodec<T>`, `RpcExternalCodecAttribute`, and the process-wide generated Codec registry.

### Compatibility and validation

- This is a source/API-breaking pre-1.0 migration. Development-time contract manifests without `wireFormatId` or the reachable `codecs` inventory are invalid and must be regenerated; no legacy fallback or compatibility shell is retained.
- MemoryPack 1.21.4 golden payloads for null, nullable/string/non-ASCII, arrays/lists/dictionaries, nested objects, empty collections, unions, and circular graphs are byte-identical under SharpPack 1.1.0.
- Local Release, Generator/Unit/Integration, collectible ALC, NativeAOT, local NuGet PackageSmoke, five-round BenchmarkDotNet, and TCP QPS/P99 validation cover the migration. No remote state or package feed was changed.

## [0.7.10] - 2026-07-22

### Added

- `SharpLinkMultiClusterClientBuilder`, `ISharpLinkMultiClusterClient`, and `SharpLinkClusterKey` coordinate multiple isolated child clients while routing each generated contract to exactly one cluster slot.
- `[assembly: SharpLinkClusterContractAssembly(cluster, typeof(Marker))]` generates a deterministic, weak-catalogued static route manifest. The generator rejects invalid cluster keys, missing generated manifests, and contradictory contract-assembly routes.
- Dynamic registration, unregister, and replacement now have explicit multi-cluster overloads. Contract-owning assemblies remain exclusive to one slot; dependency-only assemblies can remain independently owned by more than one slot.
- `AddSharpLinkMultiClusterClient` adds hosted startup, shutdown, and `ISharpLinkMultiClusterClientAccessor` without exposing child `ISharpLinkClient` instances through DI.

### Changed

- `SharpClientBuilder.Build()` keeps its existing complete generated-manifest snapshot behavior. The multi-cluster builder uses an internal filtered build context, so ordinary clients and the fixed-endpoint RPC hot path do not perform coordinator lookups or acquire new locks.
- Cluster selection is performed only by `Get<TContract>()`; generated proxies call their selected child channel directly and Protocol v2 frames, handshake capabilities, headers, and metadata do not carry a cluster key.

### Compatibility

- Ordinary single-client applications require no migration. Endpoint-cluster retry, admission, circuit-breaker, resolver, transport, authentication, and connection-pool behavior stays owned by each child client.
- Static NativeAOT routing remains manifest based. Runtime assembly registration continues to return the existing structured platform-not-supported result when unavailable.

## [0.7.9] - 2026-07-21

### 收敛

- endpoint 路径补充低基数 `sharplink.client.attempts`、`retries`、`endpoint_admission.rejected`、`breaker.open` 与 `selection.failures` metrics；默认标签不含 endpoint ID、地址、authority 或 transport 名称。
- 完成 static/dynamic topology、selector、Retry、custom admission 与 generation-scoped circuit breaker 的本地组合验证；物理 Ready/Draining 状态不因 admission 拒绝或 breaker Open 被伪装成断线。
- 文档补全迁移路径、传输限制、Retry/Breaker 语义与 0.7.x API freeze 审核说明。

## [0.7.8] - 2026-07-21

### 新增

- `ISharpLinkEndpointAdmissionPolicy`、`SharpLinkEndpointAdmissionDecision` 和 `SharpLinkEndpointOutcome` 提供 endpoint 级 TryAcquire/Report SPI；`UseEndpointAdmission` 显式启用自定义策略。
- `UseCircuitBreaker` 和 `SharpLinkCircuitBreakerOptions` 提供按 endpoint generation 隔离的 Closed/Open/HalfOpen breaker，默认关闭。

### 变更

- endpoint selection 在连接选择之前执行 admission；拒绝候选会继续选择其他 Ready endpoint，实际获得许可的 attempt 沿用 PendingCall 单一终结路径恰好 Report 一次。
- breaker 使用 monotonic time、惰性状态推进、有限采样 ring 和 HalfOpen 原子 permit，不创建每 endpoint timer；连接、拓扑与 `CheckHealthAsync` 的物理语义保持不变。

## [0.7.7] - 2026-07-21

### 新增

- `UseRetry()`、`UseRetry(Action<SharpLinkRetryOptions>)` 和 `UseRetry(ISharpLinkRetryPolicy)` 为显式标记 `[Idempotent]` 的 Unary 提供可选重试；默认最多三次、50/100/200 ms 指数退避和 ±20% jitter。
- `ISharpLinkRetryPolicy`、`SharpLinkRetryContext` 与 `SharpLinkRetryDecision` 提供同步、无 I/O 的自定义决策 SPI；非法 delay 或 policy 异常只失败当前 logical call，不影响 Client 健康。
- Retry attempt 复用既有 PendingCall 的单一完成仲裁，记录 endpoint/generation、connection、完成原因、响应是否已观测和耗时；无第二套 pending-request 表。

### 变更

- Client interceptor 仍只对一次 logical call 执行；Retry 位于其 terminal 内，每次 attempt 重新选择 endpoint，并共享入口冻结的绝对 deadline。
- 默认策略仅对 `[Idempotent]` Unary 的 endpoint 不可用、连接关闭/切换、发送失败与远端 `Unavailable` 重试；业务错误和 `ResourceExhausted`、OneWay 及所有 Streaming 不自动重试。
- 多 endpoint retry 使用调用内 `ulong` exclusion mask，优先尝试不同 Ready endpoint；尝试完当前 snapshot 后才复用候选，动态 generation 更新自动形成新候选集。
- telemetry 保持 logical call 指标一调用一次，并在 listener 存在时额外产生 `sharplink.rpc.attempt` Activity。

### 兼容性与验证

- Retry 默认关闭，因此固定单 endpoint 的既有 Unary 继续直接走原有路径；Protocol v2 wire format 和握手 capability 未改变。
- 覆盖远端 `Unavailable`、`ResponseObserved`、非幂等与 `ResourceExhausted` 拒绝重试、绝对 deadline、delay 中取消、custom policy、interceptor 一次性和 endpoint exclusion/reset。

## [0.7.6] - 2026-07-21

### 新增

- `SharpLinkEndpointSnapshot`、`ISharpLinkEndpointResolver` 与 `UseEndpointResolver` 提供版本化的动态 endpoint 拓扑；Client 对 Resolver 拥有明确的 Stop/Dispose 生命周期。
- `DelegateSharpLinkEndpointResolver` 支持连续 Watch 或单 worker 轮询，可适配 Consul、Nacos、Etcd 等应用已有 SDK，而 SharpLink 核心不引入其依赖。
- `UseDnsEndpoints` 与 `SharpLinkDnsEndpointResolver` 提供 A/AAAA Discovery、地址族筛选、规范化稳定 ID、hostname Authority、last-good 保留和可配置 refresh/jitter。

### 变更

- 动态快照以单 writer 原子协调：新增 ID 建立 generation；同 ID 的 Address/Authority 变化替换 generation；Attributes-only 更新保留连接；删除 endpoint 立即停止新调用并排空已有 Unary/Streaming。
- Resolver Watch 结束或异常后以 100 ms–30 s 的指数退避和 ±20% jitter 重启；空拓扑可恢复且继续遵守 WaitForReady、deadline、cancel 与 Stop。
- retired connection 使用独立预算。预算超出时抑制 replacement 而不强杀用户 stream，归零后 factory 恰好释放一次。

### 兼容性与验证

- Protocol v2 wire format、握手 capability、固定单 endpoint 与静态 cluster 的调用路径未改变；无新 NuGet 或第三方服务发现 SDK。
- 覆盖 add/remove/replace、属性更新、DNS、watch/retry、流排空、PackageSmoke、NativeAOT 及动态稳态矩阵；固定 TCP 五轮 A/B 的 QPS 中位数为 0.7.5 的 100.44%，P99 中位数保持 72 µs。

## [0.7.5] - 2026-07-21

### 新增

- Client 新增不可变的 `SharpLinkEndpoint`、显式传输地址和 transport factory 注册模型；`UseEndpoint`、`UseEndpoints`、`UseCluster` 及四种内置负载均衡策略可在不影响旧单端点用法的前提下构建静态端点集群。
- 静态集群支持 TCP（hostname/IPv4/IPv6）、Unix Domain Socket、Named Pipe、Shared Memory 和既有 Anonymous Pipe；端点属性可传给自定义选择器。
- 集群按端点独立维护连接、重连和健康状态，初始连接受 `MaxConnections` 与并行度上限约束；支持最少就绪端点、LeastPending、P2C、Random、RoundRobin 与自定义选择器。

### 变更

- `GoAway`、Stop 和 Dispose 进入排空流程后，新调用立即选择仍健康的端点；长 Unary 和流式调用在预算内继续完成，超出独立 retiring budget 时才被定点终止。
- 单个静态端点继续折叠为既有固定连接快速路径；多端点的成员快照仅在就绪成员增减时重建，调用路径仅读取原子快照和实时计数。
- Protocol v2 wire format、默认传输语义和现有公共配置保持兼容，未引入新的 NuGet 依赖。

### 测试与性能

- 覆盖多传输、地址族、故障/重连、GoAway 排空、所有选择策略、TLS、包使用与 NativeAOT；固定 TCP Unary 的五轮本地 A/B 中，吞吐中位数为基线的 100.27%，P99 为 104.48%，BenchmarkDotNet 分配保持 352 B/op。

## [0.7.4] - 2026-07-20

### 新增

- Protocol v2 minor 3 增加有界、确定性的压缩 wire-profile 协商；Request/Response/StreamData 只压缩业务 payload，保持路由、deadline、metadata 和 stream ID 可在分配前验证。
- `SharpLinkRuntimeOptions.Compression` 内置 Brotli，并支持线程安全的自定义 `ISharpLinkCompressionProvider` 扩展其他格式；默认按 1024 B、64 B 与 5% 三重收益门槛选择候选帧。
- `SharpLinkServerBuilder.UseAdmissionControl` 增加 Global、Contract、Method 与有界 Partition 的累计并发/速率限制，支持 TokenBucket、FixedWindow、SlidingWindow、总队列 call/byte 边界和 deadline/cancellation/Draining 联动。
- Generator 的 `RpcMethodDescriptor.ClientStreamCount` 为排队中的客户端流预留稳定 stream ID；permit 到达前按序 spool，压缩 item 按 wire bytes 记账并延迟解压。
- Generated Manifest API 升为 2；0.7.3 及更早版本的预编译生成程序集会在注册阶段明确拒绝，升级时需要重新运行 Source Generator 并编译。

### 安全性与性能

- 解压在精确有界 owner 中核验 consumed/written、原始长度和内置 Provider 完整性尾部；截断、损坏与尾部垃圾只终止当前调用/流，健康连接继续使用。
- 压缩默认关闭；普通 RPC 仅增加 Session 级空 Provider 分支，SendPump 不感知压缩且不新增锁、后台任务或每调用状态。
- Admission 默认关闭；普通调用只增加 Server 级空 controller 分支。拒绝调用保持连接健康，OneWay 不伪造执行成功，分区池机会式回收且不会永久增长。
- Provider 的 `WireProfile` 明确定义为完整 wire profile：内置 `CompressionLevel` 可按方向独立配置，dictionary 等影响解码的设置必须使用不同 profile；五轮吞吐矩阵覆盖关闭、Brotli 的 Fastest/Optimal/Smallest、收益阈值和可压缩/随机数据。

## [0.7.3] - 2026-07-20

### 新增

- Client 与 Server 增加 `ReplaceAssemblyAsync`，在完整准备和冲突验证后一次发布新路由快照，并复用现有调用/流排空、定点取消、延迟清理与 collectible ALC 引用释放机制。
- Source Generator 输出确定性的 JSON 契约 Manifest，并通过 `SharpLinkContractBaseline` 对上一版本执行 Contract、Method、DTO member、调用形态、wire type、required、enum、union 与 route 兼容性诊断。

## [0.7.2] - 2026-07-20

### 性能

- 静态 Singleton Unary 默认路径直接返回池化 `ValueTask`，不再为只负责等待响应的包装方法生成额外 async 状态机；`WaitForReady` 继续使用独立慢路径，取消、deadline、metadata、遥测和 interceptor 语义不变。
- 客户端请求操作池和 PendingCall 池改用无节点分配的并发队列，移除稳态归还时的 `ConcurrentStack` 节点分配。
- 同机五轮交替 A/B 的静态 Unary `add` 关键点中，五种传输相对 0.7.1 的 QPS 中位数提升 3.69%–13.10%，P99 均改善；进程级分配由约 482–520 B/op 降到约 138–166 B/op。
- BenchmarkDotNet 的 `Rpc_Add` 精确分配由 672 B/op 降到 360–364 B/op；剩余调用上下文与 `AsyncLocal` 分配保留认证、授权和公共 `SharpLinkCallContext.Current` 语义。

### 测试与文档

- 完整性能矩阵增加 c256/c512，并把原始输出统一到 `artifacts/performance/v0.7.2/`；补充多连接、流式、CPU、锁竞争、Allocation 与历史版本二分证据。
- 增加 0.7.2 性能报告和迁移说明。Protocol v2 wire format、公共 API、服务生命周期和资源上限均未改变。

## [0.7.1] - 2026-07-20

### 新增

- Source Generator 为每个程序集生成唯一的 RPC Manifest、定位特性与模块初始化器；Contract 程序集拥有 Descriptor、Proxy、contract-based Stub 和 Codec，Service 程序集拥有 Activator、生命周期与显式依赖。
- `[RpcService]` 服务在 Server `Build()` 时自动注册，默认生命周期为 `Singleton`；新增 `Connection` 与 `Call` 生命周期，以及 `EnableService`、`ExcludeService`、`DisableAutomaticServiceRegistration` 和 `ReplaceService` 筛选/替换入口。
- Client 与 Server 支持运行时程序集原子注册和异步安全注销；注册使用结构化成功/错误结果，注销报告剩余调用、流和框架引用释放状态。
- 动态模块支持 Running、Draining、Released/DrainTimedOut 状态、分片租约计数、依赖阻止卸载、超时定点取消，以及 collectible `AssemblyLoadContext` 卸载验证。
- Generator 增加服务声明、Contract/Method/Codec/Service 碰撞诊断，并为规范化描述生成 SHA-256 wire/schema 指纹。

### 变更

- 删除全部旧 `AddService` 重载；类型服务迁移为 `[RpcService]` 自动注册，实例和 factory 迁移为 `ReplaceService`。
- Singleton 根服务不创建调用 Scope；Connection 按物理连接惰性创建独立 Scope；Call Scope 覆盖完整 Unary、OneWay 或 Streaming 调用。
- Builder 和运行时动态 Registry 使用实例级原子快照；静态冲突优先由 Generator 报告，Build 与动态注册继续执行防御性验证和事务回滚。
- NativeAOT 保留纯静态 Manifest 路径，运行时程序集注册返回 `PlatformNotSupported`，不增加反射扫描 fallback。

### 修复

- 池化请求操作归还时立即清除 `ManualResetValueTaskSourceCore` 的 continuation，避免动态 DTO、Task 和 collectible ALC 被进程级对象池意外保留。
- Stop、Dispose、Disconnect 与并发注销复用幂等排空操作，避免重复释放、过早移除路由或同步完成留下陈旧操作。

### 性能与兼容性

- Protocol v2 wire format 未改变；静态 Singleton 保留无 Scope、无动态计数的快速路径，动态读路径不进入注册 writer gate。
- 完整性能报告和机器可读逐轮证据保存在本地忽略目录 `artifacts/performance/v0.7.1/`，不进入源代码包。

## [0.7.0] - 2026-07-20

### 新增

- 增加显式启用、仅限同机同一用户的实验性共享内存传输，以及 Client/Server Builder 的 `UseSharedMemory` 配置入口。
- 每条连接使用双向 SPSC 共享内存环传输 RPC 数据；命名管道控制通道只负责有界握手、合并唤醒、关闭和进程存活检测。
- 增加容量、SpinCount 与握手超时配置，并按 LowLatency、Balanced、Throughput profile 提供默认值；双方容量不一致时协商较小值，初始化失败不会静默降级。
- LoadTest、StreamLoadTest、Chaos、PackageSmoke 和 NativeAOT smoke 增加 SharedMemory 模式与结构化性能证据。

### 变更

- 共享内存读写管线支持环内直接写入/读取、分段回卷、有界池化 spill、背压和可复用异步等待，详细热路径计数仅在显式诊断模式启用。
- 映射采用版本化布局和当前用户私有目录；握手校验 nonce、路径、权限与布局，RPC 认证、授权、deadline、流控和心跳继续生效。

### 修复

- 修复共享等待标志可能丢失唤醒、竞态游标快照被误判为数据损坏，以及映射校验失败、握手失败和关闭路径中的资源清理问题。
- 修复通知合并、满环 spill、取消后恢复 Flush、连接强杀与 listener 重启等竞态；连接释放不会等待对端腾出环空间。

### 性能与稳定性

- macOS arm64 完成 Release JIT、独立进程 NativeAOT、包消费与两轮 10 分钟 SharedMemory Chaos；最新一轮为 4,308,099 次成功、0 次非预期失败，结束后指标、活动调用、临时映射与测试进程归零。
- Windows x64、Linux x64、macOS arm64 的 Release 构建、Unit、Generator、Integration、PackageSmoke、独立进程 SharedMemory NativeAOT 和两分钟 Chaos smoke 全部通过；正式五轮性能矩阵、2 小时及 24 小时门禁仍待完成，因此该传输仍为实验性功能，不进入正式支持矩阵。
- 32 B / LowLatency 的单轮方向性样本中，SharedMemory 在 c1/c8/c32/c128 吞吐均领先 UDS；该样本不替代正式五轮性能门禁。

完整设计、正确性证据、性能数据和未完成门禁见 `doc/shared-memory-experiment.md`。

## [0.6.10] - 2026-07-18

### 新增

- Protocol minor 2 增加可协商的 `CancellationReason` capability；协商后 Cancel 帧携带 `UserCancellation`、`DeadlineExceeded` 或 `ConsumerAbandoned`，与 0.6.9 对端仍使用空载荷 Cancel 互操作。
- Source Generator 增加 `SHARPLINK014`：Streaming 契约缺少 `CancellationToken` 时编译失败；`[NonCancellable]` 可显式豁免。增加 `SHARPLINK015`，拒绝特性与 Token 同时声明。
- `sharplink.calls.abandoned` 增加低基数终止原因标签；新增 `sharplink.responses.late_dropped` 指标。

### 变更

- 生成 Stub 将显式 Token、任意 client stream 参数和 stream 返回值都声明为框架可取消；即使业务方法标记 `[NonCancellable]`，stream pump、dispatcher、窗口等待和连接资源仍可终止。
- 服务端在请求入口把绝对 UTC deadline 换算为 monotonic timestamp，之后的到期调度和响应仲裁不再受 wall clock 调整影响。
- 服务端 deadline 使用每物理连接一个 Timer 扫描最多 1,024 个在途调用；正常完成路径不维护 timer node，也不进入 scheduler lock。
- 每连接迟到响应 Warning 最多五秒一次，并携带前一窗口被抑制的数量；迟到响应 metric 仍逐次记录。

### 修复

- 修复 deadline CTS 先取消业务 Token、后发布 `DeadlineExceeded`，导致业务取消回调可能观察到空或错误终止原因的竞态。
- 非协作调用不再仅因携带 deadline 创建 invocation CTS；超时后继续观察用户 Task，但抑制其迟到成功或异常响应。
- deadline 扫描快照同时保存 request ID 与池化 state；旧代扫描不能获取已经归池并被新请求租用的对象。
- 修复无业务 Token 的 server streaming 提前停止消费后，框架流泵可能无法及时终止的问题。
- 修复本地 stream 取消与已取得的异步 dispatch 竞争时，Cancel 可能先于最后的 WindowUpdate 到达并使对端把合法 credit 误判为协议错误、关闭健康连接的问题。
- 修复连接池扩容恰逢滚动重启失败后可能没有把零 Ready 池交给持久重连 worker，客户端永久停留在 `Draining/Reconnecting` 的问题。
- Ready connection snapshot 现在是请求选择的事实来源；全局状态发布的瞬时滞后不再拒绝已就绪连接。GoAway 排空且暂无连接返回 `Unavailable`，只有 Client Stop/Dispose 返回 `ConnectionClosed`。
- 修复 StreamManager 终止 drain 与迟到 stream 注册竞争时，dispatcher 可能挂在已经移出 map 的节点上并永久多计一个 active stream 的问题；正常注册使用两次终态读取，不增加全局锁。
- Server call admission 现在区分排空中的 `Unavailable` 与真实容量耗尽的 `ResourceExhausted`，不再把 Request accept 后发生的停机竞态错误归类为限流。

### 性能与稳定性

- `ServerCallCancellationState` 专项基准中，cooperative deadline 从实验性独立 CTS 的 368 B/op 降至 80 B/op；相对 0.6.9 的 320 B/op 也显著下降。non-cooperative deadline 从 320 B/op 降至 32 B/op。
- 增加 100,000 次客户端 Response/Cancel/Deadline 和服务端 Cancel/Response/Deadline 终态竞态、10,000 次真实 stream early-break、10,000 次 stream register/drain 竞态，以及 Stop/Connection 并发取消测试。
- 五轮交替 A/B 的所有 Unary/Streaming 场景均通过 97%/105% 门禁；`Rpc_Add` ShortRun 保持 672 B/op。
- Stream register/drain 修复的第一版全局锁实验因 QPS -7.54%、P99 +14.34% 被撤销；最终无锁版本专项五轮 A/B 为 QPS +5.31%、P99 -8.27%，两边均为零失败。
- 最终代码提交的 2 核 120 秒混合 Chaos 完成 2,632,568 次成功调用、11 次滚动重启与 0 次非预期失败；最大恢复 331ms，最终所有框架 gauge 为 0。

完整协议、迁移和证据见 `doc/protocol-v2.md`、`doc/migration-0.6.10.md`、`doc/performance-0.6.10.md` 与 `doc/chaos-0.6.10.md`。

## [0.6.9] - 2026-07-17

### 新增

- 增加 `SharpLink.ChaosTests`，覆盖混合 Unary/Streaming、提前停止消费、取消/deadline、滚动 TCP 重启、重连和最终框架指标归零。
- 增加 PR 两分钟、Nightly 两小时 Chaos 分级 Gate，以及专用宿主连续 24 小时长稳脚本与结构化 JSON 证据。
- 增加 JIT/NativeAOT 性能矩阵入口，覆盖 Transport、Profile、连接池、payload、并发、Unary、OneWay、同步/异步服务和 Streaming。
- LoadTest 增加 Empty Unary、OneWay 与 AOT-safe source-generated JSON 报告。

### 变更

- `StopAsync` 使用 graceful timeout 与固定五秒框架清理预算有界返回；不合作的业务 Task 不再永久阻塞宿主停机。
- 仍在执行的用户调用保留自身 DI scope/provider，并在真实结束后延迟清理；listener、session、Pipe、send queue 等框架资源不被业务 Task 长期保留。
- Stream Dispatcher 静态池改为每个 item 类型最多保留 1,024 个对象；大于 256 的缓冲在回池前缩回初始容量并清除引用。
- OneWay 性能证据区分正常单生产者吞吐与主动耗尽有界发送队列的 backpressure 场景。

### 修复

- 修复 server/duplex stream 提前停止消费时，WindowUpdate、Cancel、pending slot、dispatcher 租约和 send credit 之间的竞态与泄漏。
- 修复 dispatcher 在旧调用仍持有 dispatch entry 时过早回池，随后被另一调用复用并被迟到完成污染的问题。
- 修复 server/duplex stream 在异步等待连接注册期间被消费者释放后提前回池，恢复线程随后把已清空 Codec 的 dispatcher 注册到新连接的问题。
- 修复 Cancel 到达已完成调用后，响应 stream send state 未被终止并可能重新创建 credit 状态的问题。
- 修复 Session 已终止后迟到的 `NotifyConnected` 重新增加 active-connection 指标、且再无关闭机会抵消的问题。
- 修复 TLS 重连测试可能把上一连接代际尚未清空时的旧 Ready 状态误判为新连接已经可用的问题。
- 修复握手帧与后续 GoAway/首个 Request 共用同一 Pipe buffer 时，未消费尾帧被错误标记为已检查并一直等待新字节的问题。
- 重连改为实例级持久 supervisor：容量 1 的故障信号不会在 worker 退出边界丢失；同时已被立即排空的连接不再发布全局 Ready，连接归零后会持续补充替代连接直到 Client Stop。
- 修复 PendingCall 发布到 slot 后、owner active count 注册前被 Cancel/断连取走，造成 active count underflow 并击穿客户端读循环的竞态。
- 修复 NativeAOT LoadTest 报告依赖反射 JSON 序列化的问题。

### 性能与稳定性

- 五轮 TCP Unary A/B：c1 QPS +1.86%、P99 持平；c128 QPS -0.15%、P99 -1.82%，全部零错误并通过门禁。
- 五轮 Server Streaming A/B：QPS -1.45%、P99 -2.49%，零错误并通过门禁。
- `Rpc_Add` 保持 672 B/op；JIT/NativeAOT smoke 正常矩阵全部零错误，AOT publish 零 trimming/AOT 警告。
- 两分钟混合 Chaos 完成 2,943,483 次成功调用和 9 次滚动重启；以连续五次探针确认稳定恢复，最大端到端恢复 15.583 秒，非预期失败为 0，结束时所有框架 gauge 为 0。

0.6.8 → 0.6.9 没有公共 API 或 Protocol v2 wire 变更。完整证据见 `doc/performance-0.6.9.md`、`doc/chaos-0.6.9.md` 和 `doc/migration-0.6.9.md`。

## [0.6.8] - 2026-07-17

### 新增

- `ServerConnectionState` 统一拥有物理连接的 Session、认证上下文、最后接受的 request ID、每连接调用额度、取消表、连接 token 与 Handshaking/Ready/Draining/Closed 生命周期。
- `ServerCallCancellationState` 为远端 Cancel、deadline、Server Stop、连接故障和正常完成提供 first-wins 终态仲裁与独立错误分类。
- 增加连接认证隔离、幂等关闭、独立 admission、deadline timer、抛异常取消回调及 10,000 次取消/完成/Dispose 竞态测试。

### 变更

- 服务端三个 session 字典合并为单一 connection dictionary；业务请求热路径直接持有 connection state，不再逐请求查认证上下文或最后请求 ID。
- 心跳超时、同 ID 连接替换、读循环退出和 Server Stop 统一经过幂等连接关闭入口；单连接故障只取消该连接的服务调用。
- 框架任务继续被显式持有、等待和观察异常；异步用户调用改由 active-call counter 与统一 observer 收敛，不再为每个调用进入全局 Task HashSet 锁。

### 性能

- 同机 TCP c128 对同步、`Task.Yield()` 和 1 ms async 服务各执行五轮交替 A/B，全部零错误并通过 QPS/P99 回归门禁。
- 目标 `Task.Yield()` 路径 QPS +0.08%、P99 -1.54%，未达到可宣称性能收益阈值；本版本只确认全局任务集合锁从该路径消失，不宣称平均吞吐提升。
- StreamFlowController、Writer Pool、Interceptor pipeline、Throughput flush timer 与 Generated Stub Codec lookup 均因缺少触发阈值证据而保持不变。

### 修复

- 修复连接替换或心跳关闭只释放 Session、但不完成统一连接生命周期和连接级服务调用取消的问题。
- 修复携带 deadline 的调用可能把远端取消、服务停机或连接故障误归类为 `DeadlineExceeded` 的竞态。
- 修复已完成 framework task 在 Stop 获取快照前移出集合时，异常可能未被观察的问题。
- 修复 Windows Release Gate 的 TLS PFX 导出、NamedPipe 测试 flush 顺序和 PackageSmoke NuGet 源隔离问题。

完整 A/B、附加实验和保留结论见 `doc/performance-0.6.8.md`。

## [0.6.7] - 2026-07-17

### 新增

- 每条物理连接独立拥有 `ClientConnection`、pending table、request ID、monotonic deadline timer、取消源、active count 与 Ready/Draining/Closed 状态。
- `[NonCancellable]` 显式声明服务方法不支持协作取消；Source Generator 通过 `SHARPLINK004` 警告未声明 `CancellationToken` 的 RPC 方法。
- `sharplink.calls.abandoned` 指标与限频结构化日志，记录客户端已放弃但服务业务仍在执行的调用。

### 变更

- Response、error、用户取消、deadline、断连、GoAway、send failure、stream complete 与提前停止消费统一经过每连接 `PendingCallTable.TryComplete` 仲裁。
- 每连接只使用一个 monotonic timer 扫描有界 pending table；正常完成不再进入旧 timeout scheduler 的 Schedule/Cancel 锁。
- 默认 Unary timeout 仍为 30 秒并固定映射为 `SharpLinkException(DeadlineExceeded)`；`DisableRequestTimeout()` 提供真正无客户端默认 timeout 的显式入口。
- 客户端 deadline 与服务执行取消解耦：没有 `CancellationToken` 的服务调用会在客户端超时后标记 Abandoned、抑制迟到响应，并在业务任务真实结束后释放 admission 与 DI scope。
- Server/duplex stream 提前 Dispose 会发送 Cancel，并一次性释放 pending slot、dispatcher、producer、credit waiter 与 active count。

### 性能

- 同步且 `[NonCancellable]` 的服务调用不租用 cancellation state、不进入服务端 cancellation map；只有支持协作取消或真正异步未完成的调用才注册状态。
- 同机五轮 A/B 中位数相对 `v0.6.6`：c1 QPS +0.59%，c128 QPS +0.33%，c128 P99 -16.82%，`Rpc_Add` allocation 保持 672 B/op。
- 已撤销“所有服务调用无条件注册 cancellation state”的实验；其拆分测量导致 c128 相对客户端候选回退约 10%。

### 修复

- 迟到 response 不再需要永久 tombstone，也不会完成已复用的 request ID 或关闭健康连接。
- 修复提前退出 server/duplex stream 后客户端 pending、dispatcher 与 active count 泄漏。
- 修复只因请求携带 deadline 就把任意 `OperationCanceledException` 误报为 `DeadlineExceeded` 的错误分类。
- Cancel、deadline、response 与 disconnect 竞态只允许一个终态，operation 仍在调用方 `GetResult` 后才回池。

完整 A/B 环境、失败实验和最终结论见 `doc/performance-0.6.7.md`。

## [0.6.6] - 2026-07-17

### 新增

- LoadTest、StreamLoadTest 与热路径 Micro Benchmark 增加可重复 JSON 证据，记录 commit、机器、Runtime、GC、Transport、Profile、连接池、payload 与并发度。
- `IRpcBufferWriterPool.Rent(int maxWrittenBytes)` 与 session 协商后的出站帧硬上限。
- Server Interceptor 异步等待期间使用有界 ArrayPool owner 持有业务 arguments，避免越过 `PipeReader.AdvanceTo` 后读取复用内存。

### 性能

- SendPump 没有 admission waiter 时跳过容量通知锁；默认单连接池达到上限时跳过扩容状态锁。
- Generated Stub 的多段固定宽度参数改用有界 stack scratch，不再为每个参数分配临时数组。
- `SharpLinkCallContext` scope 改为值类型；基准从约 104 B/op 降至 72 B/op。
- Parser 只做帧结构验证，Metadata/Error/Handshake 等语义 payload 在消费位置解析一次；Metadata parser 基准降为 0 B/op。

### 变更

- 删除进程级 `BufferWriterPool`、`RuntimeConcurrency`、`RpcCodecRegistry`、`RpcCodec` 兼容入口；Codec、Pool 与状态容器配置只属于构建它们的 Client/Server Context。
- 删除旧 Client 调用排列组合和 CallOptions wrapper；生成代理只使用 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类 `IRpcChannel` invoker。
- 测试与示例迁移到实例级 Runtime Context、`SharpLinkBufferWriterPool` 和生成调用链。

### 修复

- Client/Server 所有业务帧、流帧与错误帧统一执行双方握手协商后的较小 frame limit；本地超限返回 `ResourceExhausted` 且不关闭健康连接。
- 修复启用异步 Server Interceptor 后请求参数可能引用已归还 Pipe buffer 的生命周期问题。

完整 A/B 环境、数据和结论见 `doc/performance-0.6.6.md`。

## [0.6.0] - 2026-07-17

### 新增

- TCP TLS 与双向证书认证，TLS 在 Protocol v2 handshake 前完成并使用独立超时。
- TLS 协议/cipher 结构化日志；默认保留平台证书链和 hostname 校验。
- `ISharpLinkClientAuthenticator`、`ISharpLinkServerAuthenticator`、二进制认证请求与 delegate adapter。
- 显式 `RequireAuthentication()` Build 校验；默认模式保持 Anonymous。
- Client/Server interceptor pipeline、实例级 `IRpcExceptionMapper` 与显式 `[Idempotent]` 方法元数据。
- `SharpLink.Client` / `SharpLink.Server` ActivitySource 与 `SharpLink` Meter；覆盖连接、调用、字节、队列、pending、stream 和失败指标。
- instance/type/factory 三类服务注册、Singleton/Scoped/Transient 生命周期与宿主 DI scope。
- Protocol v2 health-check capability、`CheckHealthAsync`、本地 readiness 和 Microsoft health checks。

### 变更

- 删除 string/bool authenticator Builder API；认证 payload 在每次重连时异步创建并受 handshake 上限约束。
- 认证上下文挂入每次服务调用；handshake 自动拒绝已过期 context，授权 helper 可在调用前再次校验 expiry/scope/tenant。
- 未注册 interceptor 时生成调用继续直达泛型 invoker；注册后客户端可修改调用选项或短路，服务端可鉴权、限流与审计。
- 未映射业务异常默认只公开 `Internal` 和通用消息；详细错误需要显式启用，stream 错误使用同一 mapper。
- Activity/Meter 无 listener 时不构建 tag collection、Activity 或调用 observer；结构化日志继续使用 `LoggerMessage` 预编译路径。
- 默认服务生命周期保持 Singleton 热路径；Scoped/Transient scope 覆盖完整调用或 stream，并在异常、取消、断线和停机时释放。
- Server 停机顺序固定为 readiness=false、停止 accept、GoAway、等待在途调用、超时取消、flush 与资源释放。

### 修复

- Source Generator 辅助 request/stream 类型加入契约名前缀，多个接口出现相同 method hash 时不再发生编译期类型冲突。
- NativeAOT 服务注册显式保留 DI 所需 public constructor，避免服务构造器被 trimming 后在首个调用失败。

## [0.5.0] - 2026-07-17

### 新增

- 可异步 admission 的 `PendingRequestTable`，完整 64 位 request ID 匹配与统一完成仲裁。
- `PooledByteBufferWriter`、明确的 frame owner 生命周期与 Context 有界 writer pool。
- Source Generator 原生 DTO/闭合集合 Codec、稳定字段 ID、未知字段跳过、required 校验与 64 层类型图边界。
- `[RpcSerializable]`、`[RpcMember]`、`[RpcIgnore]`、`[RpcRequired]`、`[RpcExternalCodec]`。
- append-only generated Codec manifest；Runtime Context 在 Build 时冻结 manifest 快照。
- Protocol v2 stream/connection 双层字节窗口、`WindowUpdate` 与单个大帧临时借用。
- 每 Endpoint 有界客户端连接池、压力扩容与 power-of-two choices 连接选择。

### 变更

- 生成代理收敛到 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类内部 Invoker；静态 descriptor/Codec 避免捕获 delegate。
- `IRpcCodec<T>.Serialize` 统一写入 `IBufferWriter<byte>`；协议回填使用 `IRpcByteBufferWriter`。
- AOT Smoke 不再依赖 MemoryPack，覆盖 class、record、struct、嵌套数组和生成 manifest。
- client/server stream sender 在额度不足时异步等待；消费、取消、超时和断连统一释放额度等待者。
- stream dispatcher 按已编码字节记账，迟到的已取消 stream data 只丢弃并计数，不重建 dispatcher。
- stream 调用固定绑定创建时的连接；`GoAway` 连接停止接收新请求并在变为空闲后退出，其他健康连接继续服务。
- LoadTest 与 StreamLoadTest 支持 `--min-connections` / `--max-connections`。

### 修复

- 长请求跨越 pending table ID 周期、乱序响应、cancel/timeout/disconnect 竞态不会误命中新请求或重复归还 operation。
- client/server stream 的额度等待、取消、迟到数据与 terminal ACK 统一收敛，慢消费者不再驱动无界缓冲。
- 多连接下单条 session 断开只失败绑定到该连接的请求，不影响其他 ready session。

## [0.4.0] - 2026-07-17

本版本合并原实施路线图的 0.4 安全基线与 0.5 Runtime/Protocol v2 重构。

### 新增

- 实例级 `SharpLinkRuntimeContext`、性能预设与不可变配置快照。
- TCP/UDS、NamedPipe、AnonymousPipe 的 Client Factory、Server Listener 和独立 Connection 模型。
- 15 字节固定头的 Protocol v2、能力协商、Ping/Pong、Cancel、GoAway、deadline 与 metadata。
- `SharpLinkCallOptions`、`SharpLinkMetadata`、扩展错误码及服务端调用上下文。
- 字节有界单写者 SendPump、强制 flush marker 与资源耗尽保护。
- Client/Server 原子生命周期、自动重连、断连单次收敛和优雅排空。
- NuGet-only PackageSmoke；`SharpLink.Sdk` 自动携带 Source Generator。

### 变更

- `ConnectAsync` 成功返回、失败抛结构化异常，不再返回 `bool`。
- Client/Server 主生命周期统一为 `StopAsync` / `DisposeAsync`。
- 默认 Unary timeout 为 30 秒；stream 默认不设置 timeout。
- 自定义 Codec、Buffer Pool 与并发配置归属实例 Context，不再互相覆盖全局状态。
- 默认认证明确为 Anonymous；删除默认 Password 和旧字符串错误 wire 格式。
- 所有正式包目标框架更新为 .NET 10。

### 修复

- Parser/Codec 在切片、分配前验证所有网络长度，非法数据稳定映射为协议/数据错误。
- pending request、stream、server concurrency、anonymous pipe offer 与 writer retention 增加硬边界。
- 修复 timeout 调度器取消墓碑导致的吞吐退化，取消节点现在立即从有界堆移除。
- 修复 send/dispose、heartbeat/stop、read/write fault 和 GoAway/new request 等生命周期竞态。
- CI 与 PackageSmoke 使用隔离 NuGet 缓存，避免同版本旧包污染验证。

### 兼容性说明

- 本版本包含公共 API 与线协议破坏性升级，不提供 Protocol v1 兼容层。
- c32 Unary QPS 为前一基线的 96.75%，保留原基线并在 0.5.1 优先处理。

## [0.2.0] - 2026-02-18

### 新增

- 引入 `RpcCodecRegistry`，支持统一注册与复用类型 Codec。
- 引入可选 MemoryPack 回退序列化器。
- 增强 blittable struct 容器编解码。

### 变更与修复

- 区分正常关闭与异常断连日志。
- 修复 Hosting 正常停机噪音与 pending request 失败传播问题。
- 本版本包含破坏性变更。

## [0.1.0] - 2026-02-12

### 新增

- 初始化 Abstractions、Runtime、Client、Server、SDK、Hosting 与 Generator。
- 支持 Unary、OneWay、客户端流、服务端流和双向流。
- 增加示例、Unit/Integration/AOT/Load/Benchmark 测试和 CI 工作流。

### 变更与修复

- 优化流式调用固定分配。
- 修复初始生命周期、取消和断连边界问题。
