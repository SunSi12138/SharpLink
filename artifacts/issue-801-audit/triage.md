# #801 — dev 3.0 死代码审计：实测证据与人工初筛

## 实际执行

- 代码基线：`dev` @ `eb99fe887cf2129d9b88441245ca0a4a6406b6c2`，审计在隔离分支 `audit/issue-801-dev-3x-deadcode` 运行。
- [成功的 GitHub Actions 工作流](https://github.com/SunSi12138/SharpLink/actions/runs/38060439793)：GitHub-hosted Ubuntu + .NET 10，**完整解决方案 Release build 成功**，Roslyn MSBuildWorkspace 语义索引成功。
- 分析 65 个项目、476/476 个 `src/**/*.cs`、945 个 source-generated documents；8,853 个生产侧声明、109,848 条语义匹配引用。
- **修订版**计数：A=542 无直接语义引用；B=303 仅 test/benchmark/demo/sample；C=358 public/protected、无生产/生成引用；D=7650 存在生产/生成引用。
- 完整证明材料：`report.json`（符号、签名、引用样本）、`tier-A.csv`、`tier-B.csv`、`tier-C.csv`、`tier-D.csv`、`report.md`。第一轮计数因不区分重载而偏低，已修正，**不要使用第一轮计数**。
- 分类是**直接语义引用索引**，不是可执行文件的完整根可达性图，也没有证明外部使用者不会调用 public API。A/B/C **不可直接整批删除**。

## 1. 优先清理：无生产、生成、测试引用的内部方法（Tier A，人工抽样核实）

| 符号 | 源码 | 检查结论 |
| --- | --- | --- |
| `RpcCodecProvider.TryGetExplicitCodec<T>` | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/Codec/RpcCodecProvider.cs#L48) | 0/0/0，明确优先候选 |
| `ClientAssemblyRegistry.TryGetProxyRegistration` | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/ClientAssemblyRegistry.cs#L64) | 0/0/0，旧辅助查询 |
| `SharpLinkClient.SendRpcCall` | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/SharpLinkClient.RpcChannel.cs#L5) | 0/0/0，较长的旧 Request 编码/发送实现，删除可减少重复协议路径 |
| `GeneratedManifestSnapshot.FromManifests`、`GeneratedClusterRouteSnapshot.FromManifests` | [Runtime](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/GeneratedManifestSource.cs#L86)、[Client](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/GeneratedClusterRouteSource.cs#L93) | 0/0/0，未用的构造辅助 |
| `RpcGenerator.HasSameCodecDefinition`、`AppendDtoSuppressedSerializeBody`、`AppendDtoExactSerializeBody` | [DtoAnalysis](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoAnalysis.cs#L20)、[DtoSerialize](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoSerializeEmitter.cs#L96)、[DtoSizing](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.DtoSizingEmitter.cs#L5) | 生成器源码内部 0/0/0，已检查独立生成文档索引；优先删冗余实现（Generator golden 测试需复验） |
| `RpcGenerator.GetReferencedInterfaceModels`、`GetReferencedServiceModels` | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Generator/RpcGenerator.ReferenceAnalysis.cs#L5) | 0/0/0，旧跨程序集模型发现入口 |
| `SharpLinkAssemblyManifestLoader.ValidateManifest` | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/SharpLinkDynamicModule.cs#L168) | 0/0/0；保留现有 central manifest validation |
| `RpcSessionExtensions.SendStreamChunkAsync / SendStreamCompleteAsync / SendStreamErrorAsync` | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionExtensions.cs#L296) | 三个扩展重载均 0/0/0；实例方法具有优先级且处理 pre-credit 和 terminal cleanup |
| `RpcSessionExtensions.SendPingAsync / SendPongAsync / SendHealthResponse` | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSessionExtensions.cs#L203) | 三个同步发送 API 均 0/0/0；生产使用对应 backpressure 版本，需连带审查旧私有封装 |
| `RpcSession.SendClientStreamChunkAsync<T>`（不接收 Codec 的首个重载） | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSession.ClientStreamPublication.cs#L5) | 0/0/0；生产使用含传入 Codec/ExactSizeCodec 的重载 |
| `SharpLinkTimer.DelayAsync(TimeSpan, CancellationToken)` 等旧重载 | [定位](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Abstractions/SharpLinkTimer.cs#L8) | 当前至少 3 个重载无引用；确认无公开契约 / 反射后纳入批量清理 |

统计格式 0/0/0 表示 production/generated/test+benchmark+examples 都没有直接引用。不等于接口方法、反射/ABI 也 0。

## 2. 只被测试/Benchmark 调用（Tier B），可迁测试后收敛

- [`RpcSession.SendStreamChunkAsync<T>`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs#L12)：生产 0，测试 22，Benchmarks 7。明确区分 **测试便利入口** 与必需的真实 pre-credit 核心流程，后者不可删除。
- [`SharpClientBuilder.UseSerializer`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Client/SharpClientBuilder.cs#L120)：生产 0，测试 5；Server 公开版本列入 3.0 breaking 决策。
- [`SynchronousBuildTransaction.OwnRange<T>` 和 `Transfer`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/SynchronousBuildTransaction.cs#L69)：生产 0，测试各 1。
- [`SharpLinkRuntimeContextBuilder.Build(bool)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/SharpLinkRuntimeContext.cs#L396)：生产 0，测试 185，Benchmark 23；测试注入便利入口，不造成明显运行热路径开销，**不必强删**。
- [`StreamManager.Register(long, dispatcher)` 等兼容重载](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/StreamManager.cs#L64)：多个重载仅测试调用，生产使用请求/stream id 精确版本，需以重载语义核查。
- [`ServerDecodeExecutor.EnqueueAsync(workItem, cancellationToken)`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/ServerDecodeExecutor.cs#L162)：生产 0、测试 14。其 keyed overload 虽有生产源码的**间接调用**，但唯一入口来自兼容/测试函数及测试；需做**传递闭包**而不能仅依赖“有一个 src 引用”保留。
- Client/Server readiness、admission、stream dispatcher 的 `ForTests/ForTesting` setter 和 diagnostic getters 数量显著，很多只被 Release 配置下运行的单元测试使用；优先区分无成本的只读 getter 与 Release 对象常驻成本。

## 3. 不止死方法：测试路径导致的生产成本

1. [`PooledAsyncStreamDispatcher<T>`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/PooledAsyncStreamDispatcher.cs#L44) **每个实例包含 9 个仅测试使用的 `Action?` 字段**，以及相应的回调检查/注入入口。x64 下光这 9 个引用就约 72B/实例（未计对象其他状态与对齐）；高频 stream dispatcher pooling 场景值得实测。**建议专用测试插桩构建或零成本 Release hook 策略**；不要简单用 `#if DEBUG` 导致目前 Release 模式的 race tests 消失。
2. [`ServerDecodeExecutor`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/ServerDecodeExecutor.cs#L13) 为兼容/测试入队创建 `SemaphoreSlim`、`CancellationTokenSource`、`TaskCompletionSource` 及 operation accounting；[StopAccepting/CompleteAsync](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/ServerDecodeExecutor.cs#L191) 仍然执行对应收尾逻辑。建议把测试迁到生产 reserved API 后，整体去除兼容子系统，再测公平性、关闭竞态、容量与分配。
3. 其他 test-only 内部字段/回调的清理以 **Release 布局、Allocations/GC、竞争窗口行为**为判断标准，不能只凭符号数量。

## 4. 公开 API（Tier C）要按 3.0 产品边界决策，不可直接删

- [`SharpLinkRuntimeContextBuilder.AddCodec<T>`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Runtime/SharpLinkRuntimeContext.cs#L379)：prod=0、generated=0、tests=31、benchmarks=16；对 standalone Context.Codecs 有真实作用，不影响 generated Contract Codec。
- [`SharpLinkServerBuilder.UseSerializer`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Server/SharpLinkServerBuilder.cs#L158)：prod=0、generated=0、tests=5；只转发 fallback resolver，考虑 3.0 public API 清理。
- [`SharpPackRpcCodec.Create<T>`](https://github.com/SunSi12138/SharpLink/blob/eb99fe887cf2129d9b88441245ca0a4a6406b6c2/src/SharpLink.Serializer.SharpPack/SharpPackRpcCodec.cs#L19)：prod=0、generated=0、tests=13，但提供 caller-owned serializer Context 的独立扩展能力，应视为可能有外部使用价值，不能按死代码判断。
- 其余 C 类大量是本就应当供用户调用的 interfaces / SDK attributes / Hosting callbacks / 公开 Builder APIs；**无内部调用通常是公开 API 的正常情况**。需逐项做 ABI/API baseline 合同评估。

## 5. 确认不能误报（Tier A 内也有潜在根）

- `ClientConnection.IStreamConsumerDeliveryGate.TryAcceptStreamDelivery`、`PendingRequestTable.IRequestEmissionFailureObserver.OnRequestEmissionFailure`、`IValueTaskSource` 回调、`IThreadPoolWorkItem.Execute`：通过接口 dispatch，直接文本调用数可能为 0。
- `SharpLinkTelemetry` 静态指标字段：虽无后续字段读取，其 initializer 向 Meter 注册 gauge，有可观察副作用。
- Socket/共享内存和 native interop 的 ABI 字段：布局所需，即使没有成员访问。
- Generated wire helpers、attribute/DI/反射、ALC module manifest/CodecHash、动态任务与 Source Generator emit strings：需要 retain root 证据，不能仅凭 0 references 删除。
- `C#14 extension` 与同名实例重载经修订版区分；第一轮的 6,924 symbols/111,391 references 计数已作废，采用第二轮 8,853/109,848。

## 后续实施边界

这个审计把 **候选找出来并保留完整引用证据**，并未对 3.0 API 去留做最终批准，也未实际删除或测移除后的性能。落地要按以下顺序：先独立移除 private/internal 真无引用成员、Build+Generator 回归；再移除依附测试便利路径的生产状态，迁移 Release race tests；最后评审 public breaking changes，更新 `eng/public-api/2.0.0` 对照与 migration docs，执行 package/AOT/trim/protocol/性能回归。实施前应对新的 dev HEAD 复跑审计。
