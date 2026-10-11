# Issue #801 — current 3.0 cleanup priority backlog

Baseline `dev` @ `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`. A/B/C/D are **reference classifications**, not an automatic deletion plan. GitHub Actions checkout source was temporarily modified only for verification. No production file has been merged to dev.

## Implementation progress on merged-#799 3.0 dev (2026-10-11)

Historical semantic/experiment evidence below was captured on pre-#799 dev `eb99fe88`. The initial implementation branches were based on `dev` `a1f9ee2a` (with #799 already merged). **PR #806 was subsequently merged into dev** as `d4cddbb17ba4dd3fc28046658db9fceafcac8d9f`. All other #801 cleanup PRs remain unmerged; PR #810 was rebuilt to remove the overlap with #806.

- [#806 — retire standalone/endpoint dynamic Codec APIs](https://github.com/SunSi12138/SharpLink/pull/806), **MERGED** into dev at `d4cddbb1`; exact public API/package/desktop AOT checks passed.
- [#807 — remove ServerDecodeExecutor compatibility queue](https://github.com/SunSi12138/SharpLink/pull/807), Ready; 3 source/test files, `+137/-318`, full Unit/Gen/Integration and PR gates passed.
- [#808 — compact SharpLinkClient 10 test-only callback fields](https://github.com/SunSi12138/SharpLink/pull/808), Ready; source and tests, Full Release/Unit/Gen/Integration, PR gates passed.
- [#809 — compact PooledAsyncStreamDispatcher 9 test-only callback fields](https://github.com/SunSi12138/SharpLink/pull/809), Ready; corrected initial file-LOC gate regression; current exact-head PR Fast/Package Smoke/CodeQL passed.
- [#810 — delete 44 remaining verified unused non-public methods](https://github.com/SunSi12138/SharpLink/pull/810), **rebased Draft** on dev `d4cddbb1` after #806 merged. Of the original 45 deletion candidates, `RpcCodecProvider.TryGetExplicitCodec<T>` was already removed by #806 and is now deliberately omitted. The frozen 45 remaining candidates removed 44 bodies, retaining one bodyless logger partial. [Merged-base exact-source validation #38116153659](https://github.com/SunSi12138/SharpLink/actions/runs/38116153659): full Release 0W/0E; UnitTests 2034, Generator 271, IntegrationTests 453 passed. New PR Fast/Package/Codec/Mobile checks running, and [fresh Windows/macOS exact-head NativeAOT #38116397841](https://github.com/SunSi12138/SharpLink/actions/runs/38116397841) scheduled. Older 45-method cross-platform evidence is historical and not attributed to this new SHA.
- [#811 — remove IRpcChannel's fake client stream sink inheritance](https://github.com/SunSi12138/SharpLink/pull/811), Ready; 3.0 Abstractions ABI snapshot + migration guide; Release/Unit/Gen/Integration and PR gates passed.
- [#812 — remove Server reflection-only lifecycle alias](https://github.com/SunSi12138/SharpLink/pull/812), Ready; migrate test to existing coordinator diagnostic method; PR gates passed.
- [#813 — remove test-only unbounded RPC error sender](https://github.com/SunSi12138/SharpLink/pull/813), Ready; deadline tests now await the production backpressure method; PR gates passed.

**Exact-head cross-platform NativeAOT:** [Windows x64 + macOS ARM64 GitHub Actions matrix #38115419654](https://github.com/SunSi12138/SharpLink/actions/runs/38115419654) **10/10 PASS** on the pinned source SHA of #807–#811. Each job ran real NativeAOT TCP, shared memory, SharpPack and pre-credit scenarios. Individual PR comments link job logs and artifacts. These claims concern desktops, not mobile physical hardware. #810's mobile CoreCLR matrix remains separate.

The remaining PRs require human review before merge. PR #806 has already been merged via repository review and is no longer pending. [#805](https://github.com/SunSi12138/SharpLink/issues/805) covers public→internal and sealed design separately; do not mix it into these implementation PRs.

## Status update: executed next priority groups

- **ServerDecodeExecutor test compatibility enqueue subsystem — independently PASS.** Replaced the test-only blocking queue with the real production reserved-before-publication queue, migrated two unit-test files, and removed the extra SemaphoreSlim/CTS/TCS plus operation accounting and entry branch. Source/test experiment delta **3 files, +137/−318 lines**. [Full UnitTests 2018, Generator 269, Integration 453 and pristine build pass](./decode-queue-compatibility-teardown.md); [three-platform NativeAOT, trim, 8-package public API and external consumers pass](https://github.com/SunSi12138/SharpLink/actions/runs/38107400102). A controlled Linux .NET 10 ctor allocation trial measured **832 B → 584 B median**, 17→13 instance fields, saving **248 B/constructor (sample-specific; not throughput)**. [Raw allocation proof](https://github.com/SunSi12138/SharpLink/actions/runs/38107583355).
- **PooledAsyncStreamDispatcher<T> nine test hooks → one lazily allocated Action[] pointer — PASS** Release UnitTests 2018 and Generator 269, baseline comparator. [Proof](./lazy-test-hook-validation.md). **64B theoretical x64 instance-field reduction**; no actual throughput/retained heap measurements.
- **SharpLinkClient ten session-refresh/failure test hooks → one lazily allocated Delegate[] pointer — PASS** Release UnitTests 2018, Generator 269, IntegrationTests 453, Linux NativeAOT and pristine build. [Proof](./client-session-hook-validation.md). **72B theoretical x64 client instance-field reduction**; hot-path speed still unmeasured.
- **3.0 IRpcChannel false streaming interface inheritance — PASS as a breaking-change experiment** Release UnitTests 2018, Generator 269, Linux NativeAOT after unlinking the inheritance and deleting three fake/forwarding channel methods while retaining ClientConnection's real sink. [Proof](./rpc-channel-3x-api-validation.md). Requires approval of external API break/migration docs, not a silent 2.x change.
- **Earlier 45 non-public zero-direct-reference methods**: not modified in dev, but validated on all three desktop NativeAOT platforms plus trimmed and NuGet package consumers. [Proof](./native-safety-validation.md).
- **Open policy calls:** `SharpLinkRuntimeContextBuilder.AddCodec<T>`, Server `UseSerializer` and runtime `UseCodecResolver` fallback graph; these are genuine standalone runtime extension behaviors and cannot be automatically labeled dead solely from in-repository references.

The two test-hook refactors and the decode-queue overhaul are three **separate** implementation groups, and should have independent, reviewable PRs and benchmarking gates rather than being combined with the unrelated 45-method cleanup.

## Historical clarification recorded before #799 was merged (2026-10-11)

**Prior issue/backlog wording “runtime fallback” should not be read as the unmanaged reflection fallback.** Merged PR [#799](https://github.com/SunSi12138/SharpLink/pull/799) removes *automatic* reflection-based `UnsafeBlitCodec<T>` resolution for arbitrary unregistered unmanaged types but explicitly retains the **application-supplied** `UseCodecResolver(Func<Type, IRpcCodec?>)` callback invoked by `RpcCodecProvider`. Generated RPC already uses owner-local compile-time Codec/Adapter bindings and intentionally ignores both endpoint `UseCodecResolver` and standalone `AddCodec`.

Prioritize **removing misleading Server `UseSerializer`** in the major-version API review: it merely forwards an endpoint resolver unused by generated RPC.

For standalone `SharpLinkRuntimeContextBuilder.AddCodec<T>` and `UseCodecResolver`, decide whether 3.0 retains any standalone `context.Codecs.GetCodec<T>()` extension policy. PR #799's own [3.0 migration guide](https://github.com/SunSi12138/SharpLink/blob/work/762-generated-unsafe-blit/doc/unsafe-blit-3.0-migration.md) originally used `AddCodec(new MyPayloadCodec())` as the recommended standalone migration after removing unmanaged reflection fallback; deleting it would be a **deliberate feature withdrawal** rather than merely pruning an unused generated RPC knob. Coordinate this decision and its replacement/upgrade guidance with #799 (now merged into dev; #806 updates migration), rather than leaving incompatible migration instructions and 3.0 public API baselines.

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
