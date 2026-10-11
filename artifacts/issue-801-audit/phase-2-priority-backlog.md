# Issue #801 — current 3.0 cleanup priority backlog

Baseline `dev` @ `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`. A/B/C/D are **reference classifications**, not an automatic deletion plan. GitHub Actions checkout source was temporarily modified only for verification. No production file has been merged to dev.

## Established implementation evidence

| Candidate | Actual proof | Recommendation |
|---|---|---|
| **45 specific non-public methods** (enumerated in [verified-deletion-probe.md](./verified-deletion-probe.md)) | Full Release build 0 warnings; UnitTests 2018/2018; Generator 269/269; pristine baseline 2018/2018; Linux x64 NativeAOT, full trim, collectible ALC/dynamic plugin 9 targeted cases all pass. | Highest-confidence 3.0 cleanup candidates, in small reviewable PR batches. Platform/pack/ABI validation still needed. |
| **PooledAsyncStreamDispatcher<T> test-hook data footprint** | Nine per-instance test-only Action pointers replaced with one lazily allocated Action array pointer. Release compile + UnitTests 2018/2018 + Generator 269/269 + pristine comparator pass. [Proof](./lazy-test-hook-validation.md) | Implement separately after real allocation/throughput checks; projected 64B/instance pointer-field reduction, not measured performance. |
| **IRpcChannel inheriting IRpcClientStreamSink** | Four-file proof: unlink inheritance and delete three fake/forwarding channel methods, preserve connection-bound real sink. Release 2018 UnitTests + 269 Generator Tests + Linux NativeAOT all pass. [Proof](./rpc-channel-3x-api-validation.md) | Explicit **3.0 breaking API decision**, public-api baseline and external adapter migration. |
| **ServerDecodeExecutor compatibility/test enqueue path** | Production invokes keyed reserved queue API; tests use compatibility `EnqueueAsync`; constructor still allocates compatibility SemaphoreSlim, CTS and TCS and stop/drain tracks compatibility operations. | Next highest-value **runtime-cost** cleanup: first migrate scheduler fairness/race tests onto the production reserved API, then delete the compatibility branch. Must preserve cancellation/close guarantees. |
| **SharpLinkRuntimeContextBuilder.AddCodec<T>** | No production or generated call; 31 test + 16 benchmark direct references. Real standalone Context.Codecs effect, intentionally not override for generated endpoint RPC. | **3.0 product/API decision**, not dead-by-proof; plan external migration or deliberate continued standalone support. |
| **SharpLinkServerBuilder.UseSerializer** | No production/generated call; five test references. Client-side equivalent is internal. It forwards runtime fallback resolver but does not override Generated RPC codec binding. | Candidate to drop public method in 3.0 if fallback is not a supported RPC API. |
| **SharpPackRpcCodec.Create<T>** | 13 test references and no production/generated direct usage, but intentional caller-owned SharpPack serializer-context factory. | Do not delete merely for repo zero-production-call status; retain unless explicit standalone API deprecation. |

## Test-only symbol classes — current compiler-bound index

| Project | Tier B symbols with test/bench/example callers but no direct production/generated callers |
|---|---:|
| SharpLink.Server | 125 |
| SharpLink.Runtime | 98 |
| SharpLink.Client | 60 |
| SharpLink.Hosting | 9 |
| SharpLink.Abstractions | 4 |
| SharpLink.Shared | 2 |
| SharpLink.Serializer.SharpPack | 1 |
| **Total** | **299** |

Of these, *Server* includes **77 properties** (many diagnostics/admission inspection surfaces), *Runtime* includes **72 methods / 24 properties** (test fixture/compatibility overloads), and *Client* includes **47 methods / 13 properties**. The absence of Tier B field symbols does **not** indicate there are no test-only production fields; hooks referenced by production paths still classify as Tier D (example: PooledAsyncStreamDispatcher nine callback fields). This is why the hook footprint audit must complement the semantic-reference list.

## False positive protections established by actual verification

- Native/interop layout fields, Static Meter registration instruments, attribute-discovered generated code, plugin registration and public interface entries need non-call reachability roots.
- C#14 extension-block `ValidateTcpPort` / `ValidateBacklog` helpers were incorrectly tagged zero-reference by initial symbol-key lookup; dropping them caused 12 compilation errors. Source-location fallback/explicit retain prevents recurrence.
- Linked `SharpLink.Shared` sources have references in multiple compilation contexts. Removing `RpcBuiltinCollectionWireCatalog.TryGet` broke the Generator build even though original index saw zero references.
- `SharpLinkServer.DisposeAllSessionsAsync` private alias was called from tests only using `GetMethod("DisposeAllSessionsAsync")`; removing the alias made shutdown tests fail through an observable cleanup exception. Reflection-name roots must be preserved until tests are migrated.
- Interface dispatch, `IValueTaskSource` and ThreadPool callback methods can show no direct calls and still be active runtime methods.

## Remaining work before issue completion

1. Windows x64 and macOS ARM64 NativeAOT for the 45-method exact deletion set: [cross-platform Actions](https://github.com/SunSi12138/SharpLink/actions/runs/38103915186).
2. Package smoke, external 3.0 API migration analysis and ABI/public-api comparison, all test-only compatibility paths.
3. Real microbenchmarks and allocations/latency regression for lazy test-hook storage; investigate `ServerDecodeExecutor` compatibility queue removal after test migration.
4. Separate reviewable PRs for the 45 compiler-plus-runtime-verified deletions and any approved 3.0 public API changes; rerun against current dev HEAD at implementation time.

Raw symbol evidence remains in `report.json` and `tier-{A,B,C,D}.csv`.
