# #732 bounded method-facts investigation

## Scope and decision

Baseline: `ca993a1aa89864755e10d1e35030a612b43d6069` (`dev`; includes #796, does not depend on unmerged #797). Generated invocation switches, public APIs, generated ABI, wire format and method registry are unchanged.

- **Candidate A: No-Go.** A context-descriptor helper was also used on static calls. Despite removing a query on intercepted calls, repeated static/plain controls exposed extra common-path work. Query count alone is not a performance result.
- **Candidate B: qualified Go for the narrow PGO/JIT optimization, ready for user review as an alternative patch.** Only the existing `registration.HasModule` block reuses `SharpLinkServerInvocationContext.Method`. The production patch is 3 additions / 1 deletion, with no helper or added state. Static tracked/telemetry/fallback code remains baseline.
- This is a bounded current-2.x-compatible alternative to existing PR #754, which changes the generated API/ABI for 3.0. Both touch the same dynamic classification block. B is not additive to #754. No competing production PR was opened, and #754 was not modified. User choice remains pending.
- This does not finish every #732 goal. Admission, OneWay shape checks and separate cancellation queries remain. No full-RPC speedup is claimed.

## Exact code and fixture identity

A validation head: `90ab969e8047b6da3d92760e9dd8230b9cddd528`, branch `validation/732-method-facts`.
B validation head: `7dab2cea0d3e018e0f7c0c431cfe303a5b7c09dd`, branch `validation/732-module-method-facts`.
The complete reviewable code + 14 test cases are also bundled in `production-code-and-tests.patch` (no reports or workflows in that patch). Both validation heads leave production source unchanged; their runner applies `candidate.patch` in one detached worktree only. B production source SHA-256 is `b9e104b182556737c758f6843a6f213f7223110e883facab0b52387a57bb89e9`.

`source-hashes.txt` and binary SHA-256 files identify local sources/images. The archived V1 timing source checksums were checked against the original portable PDB. Hosted V2 adds cold characterization entry points and source-generated JSON, so its image sizes and hashes are separate. Do not attach hosted V2 binary identities to local V1 timings.

## Query characterization

D = `TryGetMethodDescriptor` calls; I = 0/1 for server interceptor. Counts are actual server-dispatch observations, with a separate counting custom stub. The final generated invocation method switch remains once per successful invocation. C (`SupportsCancellation`) is independently counted and is not a field of `RpcMethodDescriptor`.

| Successful path | Baseline D | B static D | B dynamic D |
| --- | ---: | ---: | ---: |
| Unary / two-way streaming, admission off | 1 + I | 1 + I | 1 |
| Unary / two-way streaming, immediate or queued admission | 2 + I | 2 + I | 2 |
| OneWay, no or immediate admission | 2 + I | 2 + I | 2 |
| OneWay, queued admission resume | 3 + I | 3 + I | 3 |

C = 1 on reaching invocation setup if wire Cancellable or dynamic module, otherwise 0. Custom-stub default true and independent false are preserved. Telemetry listener on/off does not change D because the descriptor argument is evaluated before telemetry startup. Client/duplex rows in the count fixture characterize metadata only; real typed-stream regressions are separate.

Rejection/error boundaries, unchanged by B:

- Unknown service: D=0; no invoke. Admission rejection/partition failure: D=1, C=0, no invoke.
- Early expired deadline or draining module: Unary D=0; OneWay D=1 because shape is resolved first. A queued leg adds its earlier admission lookup.
- Capacity/decode rejection after admission: Unary D=0 without admission, D=1 with it; OneWay D=1 (queued resumed OneWay D=2); C=0, no invoke.
- Unknown OneWay stops on the failed initial query and terminates/drains. Two-way uses its conservative fallback and final stub dispatch/error behavior. Error mapping without an invocation context can add D=1; stream error mapping can do so per error.
- The fixture row named `unknown-method` means a custom stub with a missing descriptor and controlled generic failure. It does not prove the generated unknown-method wire error payload.
- Queued admission re-reads registration after awaiting. Persistent decode retains its original exact registration snapshot. B changes neither ownership boundary and creates no cross-registration cache.

Local count matrices each have 80 success combinations per arm (5 kinds × interceptor × module × admission × cancellation), plus A's 40 edge rows per arm. B only removes one D for dynamic + intercepted paths that reach classification; C/invocation counts remain equal. Hosted assertions cover both JIT and AOT.

## Local experiment and calibration

.NET SDK 10.0.112 / runtime 10.0.12, Linux x64, AMD EPYC 9V74 cloud VM. `DOTNET_PROCESSOR_COUNT=2`. The paired pass is pinned to CPU 2, alternates process order, uses 7 pairs and 5,000,000 operations per cell. Separate A/A calibration uses the exact same baseline binary on both arms (5 pairs × 2,000,000 operations). No concurrent local build/test workload was run during timed passes.

The timed boundary is real private `CreateCallContext` + `InvokeServiceAsync` through `UnsafeAccessor`, 16 rotating generated no-payload Unary methods, final generated dispatch switch, pass-through interceptor, ambient context push/pop, and actual dynamic module acquisition/release. No counter, reflection invocation or boxed argument array is in the timed loop. Fresh intercepted context allocation is included. This is a local dispatch boundary, not transport latency or a production workload distribution.

Tables report median of paired candidate/baseline ratios, which is not the ratio of independent medians. Negative is faster. Raw rows include ns/op, CPU ns/op and allocation.

### Candidate A, pinned confirmation

| Profile | Dynamic intercepted | Dynamic plain | Static intercepted | Static plain |
| --- | ---: | ---: | ---: | ---: |
| JIT PGO | −5.28% | +1.09% | +1.99% | **+11.42%** |
| JIT FullOpts | −1.08% | −5.67% | −6.97% | **+5.18%** |
| NativeAOT | +0.41% | +0.15% | +2.84% | −0.12% |

Static/plain lost all 7 pairs in both JIT profiles. The broader first pass (`rows.jsonl`, `summary.json`) is retained, not discarded. A is not proposed for adoption.

### Candidate B, independently measured after freezing

| Profile | Dynamic intercepted | Wins / 7 | Dynamic plain | Static intercepted | Static plain |
| --- | ---: | ---: | ---: | ---: | ---: |
| JIT PGO | **−5.69%** | 6 | −1.34% | −0.70% | −2.15% |
| JIT FullOpts | −2.84% | 5 | −1.24% | +2.15% | −1.08% |
| NativeAOT | −0.11% | 4 | −0.78% | +0.03% | −1.76% |

Target ns/op independent medians: PGO 347.56→325.20; FullOpts 443.20→424.57; AOT 411.49→406.68. Allocation remains 344 B/op intercepted, 72 B/op plain; tiny fractional differences are fixed process-measurement overhead.

A/A calibration shows substantial VM variation: median biases approximately ±4% for most cells, with a +12.61% AOT dynamic/plain outlier and wide individual ranges. Accordingly, FullOpts and AOT results are not asserted as reliable gains, and small control shifts are not attributed to the patch. AOT is neutral. The independent hosted matched pass below corroborates B's PGO result.

Reproduce this local paired shape with the preserved V1 binaries/build and this directory's `run.py`:

```sh
AFFINITY=2 PAIRS=7 ITERATIONS=5000000 \
SHAPES=plain-context,intercept-context,dynamic-plain-context,dynamic-intercept-context \
CANDIDATE=module VARIANT=B ROWS=b-pinned.jsonl python3 run.py
```

The public hosted runner reproduces independent baseline/candidate V2 builds without relying on these local paths/binaries.

## Independent hosted B pass (V2)

SDK/runtime match the local versions; Ubuntu 24.04 GitHub-hosted runner, no CPU affinity requested. Same arms/shape/alternating ordering, 7 pairs, 2,000,000 operations per cell. Fresh baseline and B source hashes match exactly. [Artifact 11637722565](https://github.com/SunSi12138/SharpLink/actions/runs/37972549941/artifacts/11637722565), outer ZIP SHA-256 `28a7f2388016480b8b36c9503bc4e937c0fba3ba79e601a315351047f637ee27`, 695,287 bytes. Compact raw/summary copies are under `hosted-b/`.

| Profile | Dynamic intercepted | Wins / 7 | Dynamic plain | Static intercepted | Static plain |
| --- | ---: | ---: | ---: | ---: | ---: |
| JIT PGO | **−3.72%** | 6 | −0.79% | −0.34% | +0.35% |
| JIT FullOpts | −1.18% | 4 | −3.02% | +0.67% | −1.86% |
| NativeAOT | +0.78% | 3 | −0.76% | +1.55% | −0.10% |

Target PGO CPU/op paired median is −3.90%, alongside wall-time −3.72%. Target independent ns medians are 387.32→377.91. The repeated PGO benefit occurs without A's repeated static/plain penalty. FullOpts and AOT remain neutral/uncertain; no improvement is claimed there. Hosted V2 AOT image 15,634,240→15,634,360 bytes (+120), distinct from V1's +152 bytes. No per-op allocation increase.

## Code size and retention cost (local V1)

| Metric | Baseline | B |
| --- | ---: | ---: |
| InvokeServiceAsync IL | 492 | 514 |
| InvokeServiceTrackedAsync IL | 121 | 121 |
| FullOpts InvokeServiceAsync native bytes | 2455 | 2497 |
| FullOpts tracked native bytes | 737 | 737 |
| AOT InvokeServiceAsync native bytes | 2409 | 2465 |
| AOT tracked native bytes | 640 | 640 |
| Whole AOT image bytes | 15,576,224 | 15,576,376 |

Acquisition async state remains 280 bytes / 17 fields; lease async state remains 216 bytes / 17 fields. No additional async field/reference retention. Descriptor is 32 bytes; nullable descriptor is 40 bytes. A's helper had a larger common-path cost (tracked FullOpts 737→800 bytes, image +184 bytes); B image +152 bytes. Whole-image sizes are build/layout-specific, not ABI claims. Generated code is unchanged.

## Correctness and validation

- Author B Release build: 0 warnings/errors; invocation tests **32/32** including 14 added cases (9 in hosted candidate test file + 5 supplemental gated lease cases).
- Supplemental `ServerInvocationModuleDescriptorTests.cs` checks all five method kinds, real client/duplex stream route reservation, live module call/stream counters, drain waiting while invocation is gated, and zero counters/routes after release. These 5 tests were run locally after the performance head was frozen; they are not part of hosted `7dab2cea`, but all are included in the successful matched unit confirmation `70598d1c`.
- Independent reviewer found no B semantic, ABI, source-layout or retention defect; reviewed new tests and raw paired rows. Reviewer independently confirmed that both local and hosted raw paired rows support a qualified narrow JIT optimization. The initial unit timeout is retained as non-reproduced and unclassified, not silently resolved.
- A generated TCP regression fixture passed all 72 cells: both arms, JIT/AOT, six RPC shapes (Unary, OneWay, cancellable, upload, download, duplex), and plain/intercepted/admission+telemetry modes. This uses static services and is regression evidence, not dynamic-module throughput evidence.
- A hosted run [37966268258](https://github.com/SunSi12138/SharpLink/actions/runs/37966268258) failed before timing on a NuGet NU1301 connection reset restoring package validation. Its logs are not performance evidence.
- B hosted run [37972549941](https://github.com/SunSi12138/SharpLink/actions/runs/37972549941) finished with performance/count/regression evidence intact: **480 exact assertions passed**, **72/72 TCP regression cells completed**, build succeeded. Full unit suite: **1901/1902**, one failure in `PooledAsyncStreamDispatcherTests.EarlyDisposeShouldNotPoolWhileProducerIsDecoding` at `entered.Wait(3s)`, before server invocation or disposal. Original failure remains recorded in `hosted-b/tests.log`; the run is failed, not green.
- That test directly uses unchanged Runtime code, queues a producer with `Task.Run`, then blocks waiting for it. Scheduling sensitivity with `DOTNET_PROCESSOR_COUNT=2` and parallel tests is a hypothesis, not yet a proven cause. No test timeout/source was changed.
- A bounded matched unit-only confirmation runs original baseline and B full suites plus the exact failing test in three fresh processes per arm, under the same environment. It includes the 5 supplemental B ownership tests: [37974317092](https://github.com/SunSi12138/SharpLink/actions/runs/37974317092), head `70598d1c0589c0089ffcae30aeaee47ef44e7f08`. **Passed:** baseline **1893/1893**, B **1907/1907** (all 14 new cases), exact test **3/3** fresh-process passes on each arm. The failed test source SHA-256 is identical across arms. [Artifact 11638213878](https://github.com/SunSi12138/SharpLink/actions/runs/37974317092/artifacts/11638213878), outer ZIP SHA-256 `588c812e08919913b4262c40b9beae6160e5b34039787c184c589d38b75248cc`, 4,650 bytes; logs under `hosted-unit/`. This establishes clean matched validation and non-reproduction, not the root cause or a fix for the first run. The initial timeout remains unclassified; it is not demonstrated to be a B regression or a proven baseline flake. No further performance experiment or variant was run.

## Remaining decision

Do not close #732 based on this narrow slice. The review bundle and evidence are ready, but no production PR or merge was performed. First choose whether to keep the broader breaking 3.0/API5 proposal in #754 or consider B's small current-2.x-compatible alternative. B does not replace admission/cancellation/OneWay lookups with a universal resolved-facts contract, and no such complexity was added merely to reduce a counter.
