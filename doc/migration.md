# 迁移到 2.0

SharpLink 2.0 将进程内 Generated ABI 从 1.1.x 的 API 3 原子升级为最终基线 API 5，同时保持网络 Protocol v2 不变。API 4 是 2.0 开发期的中间生成面（codec 架构落地前的冻结点），从未随包发布；2.0 Runtime 同样在启动期拒绝它。升级前让同一进程中的全部 SharpLink 包使用 2.0，并在独立环境完成 Client/Server 互操作、AOT、负载和故障测试。

## Generated ABI（API 5）与重新生成

2.0 Generator 只生成 API 5，2.0 Runtime 也只接受 `Generated API = 5`、`Protocol = 2`。1.1.x 生成程序集是 API 3，2.0 早期开发树生成的是 API 4；2.0 会在 materialize Manifest 或发布任何运行时资源前明确拒绝两者，不提供隐藏开关、双路径或环境变量回退。版本校验只发生在 assembly load / registration / startup 边界，不进入任何调用热路径。

升级必须同时完成：

1. 把 SDK、Abstractions、Runtime、Client、Server、Hosting 和 serializer adapter 统一为 2.0。
2. 删除所有契约、服务和插件项目的旧 `bin`、`obj` 与缓存生成源码。
3. 重新构建全部 contract assemblies 和 service assemblies。
4. 重新构建并重新部署全部 plugin assemblies；不要把 1.1.x、API 4 与 2.0 生成程序集装入同一进程。

旧 artifact 在注册/启动期收到稳定的 version mismatch，例如：

```text
IncompatibleManifest: Manifest compatibility mismatch: API 4/5, Protocol 2/2,
Generator '2.0.0'. Action: delete stale generated outputs, then regenerate and
rebuild this assembly with the SharpLink SDK version that matches the current
Runtime.
```

`Assembly`、`LoadContext`（dynamic）、`Expected/Actual Generated ABI`、`Expected/Actual Protocol`
与 `GeneratorVersion` 字段在所有入口一致。修复方式始终是重新生成：删除旧输出，用当前
2.0 SDK 重新构建，而不是回退包版本或寻找兼容开关。

自动生成代码的用户不需要手写 Bridge。手写生成基础设施的高级用户需要同步采用 API 5：程序集 locator 使用包含 Manifest 类型、`apiVersion: 5`、`protocolVersion: 2` 和 Generator version 的自描述构造函数；`IRpcStub` 接收 `IRpcGeneratedServerBridge`，响应写入 `IBufferWriter<byte>`；`SharpLinkGeneratedContractDescriptor.StubFactory` 接收 `IRpcCodecProvider`；生成的 DTO Codec 实现 `IRpcCodec<T>` 与 `IRpcSizedCodec<T>`；自定义 Codec 绑定使用 `RpcCodecAttribute`/`RpcCodecImplementationAttribute` 并带 schema identity。

Generated ABI 不参与网络握手。1.1.x Client 与 2.0 Server、2.0 Client 与 1.1.x Server 仍可通过 Protocol v2 互操作，但每个进程只能加载与本进程 Runtime 匹配的生成程序集，并且两端契约的 wire schema 必须兼容。

## Runtime engine API boundary

`IRpcSession`、`IStreamManager`、raw stream dispatcher interfaces、`PooledAsyncStreamDispatcher<T>`、
`RpcSession`、`StreamManager` 和 `RpcSessionExtensions` 不再是公开扩展面。不要构造或控制 Session、读取其 PipeReader、注册 raw
dispatcher、设置 peer activity，或直接发送 protocol control frame。自定义传输应实现
`ITransportConnection` 并经 `IClientTransportFactory` 或 `IServerTransportListener` 配置到 Builder；
generated server code 继续使用 API 5 的 `IRpcGeneratedServerBridge`。完整的 public API diff、保留 SPI
和 ownership 说明见 [`runtime-phase-16-engine-api.md`](runtime-phase-16-engine-api.md)。

## Builder 构建计划与单次使用

`SharpClientBuilder` 和 `SharpLinkServerBuilder` 现在在 `Build()` 中先冻结完整
BuildPlan，再 materialize framework-owned 资源并提交所有权。Builder 本身是一次性的：无论
Build 成功或失败，后续的 `Build()` 或 `Use*`/`Add*` 调用都会抛出
`InvalidOperationException("This SharpLink builder has already been consumed.")`。需要另一个
Client 或 Server 时，创建新 Builder，不要修改或复用已经 Build 过的实例。

Client topology 也必须在第一次配置时确定。`UseTransport`、`UseEndpoint`/`UseEndpoints` 和
`UseEndpointResolver` 不能混用，也不能重复配置同一种 topology；第二次调用会立即失败。静态
endpoint 与 manifest source 只在 Compile 时取一次快照，随后修改原 collection、attribute 字典或
options 不会影响已经编译的 plan。多集群会用同一个 child plan 同时执行预算检查和 materialize，
不再存在 endpoint 预检缓存。

详见 [`runtime-phase-11-build-plan.md`](runtime-phase-11-build-plan.md)。

## Client readiness API

`ISharpLinkClient` 新增 `GetReadinessSnapshot()` 和 `WaitForReadinessAsync(...)`。内置 Client 提供固定、静态与 resolver 拓扑的精确快照；`ConnectAsync` 仍只承担 connectivity，不会等待多 endpoint 收敛。已有第三方 `ISharpLinkClient` 实现无需重新编译即可继续加载：接口默认实现会明确抛出 `NotSupportedException`，不会伪造单 endpoint 数据。包装或代理实现如果希望支持 readiness，应转发这两个成员并保留调用方独立取消与终止状态语义。

## 包依赖变化

`SharpLink.Sdk` 2.0 只依赖 `SharpLink.Abstractions` 并携带 Analyzer/Source Generator，不再传递引入 `SharpLink.Runtime`。纯契约项目继续只引用 SDK；Client、Server 或 Hosting 应用引用相应应用包，由应用包引入 Runtime。直接使用 Runtime API 的库必须显式引用 `SharpLink.Runtime`。

官方 SharpPack adapter 的公开类型从 `SharpLink.Runtime` 命名空间移动到 `SharpLink.Serializer.SharpPack`。例如：

```csharp
[assembly: RpcCodecAdapter(
    typeof(ThirdPartyGraph),
    typeof(SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter))]
```

## 从 0.7.x

- MemoryPack 扩展和 `RpcExternalCodec` 已删除。复杂图使用通用 Codec Adapter SPI；官方实现为 `SharpLink.Serializer.SharpPack`。
- 多 endpoint、Resolver、Retry、Circuit Breaker 和 multi-cluster 使用当前 Builder API；不要依赖旧实验接口或进程级默认 serializer state。
- 动态模块必须提供兼容 generated Manifest，并遵守注册、替换、排空、注销和 collectible ALC 所有权。

## 从早期 0.8.x

- 使用具体 `SharpLinkErrorCode`；`Unknown` 不能作为 wire error 或 `SharpLinkException` code。
- required/non-nullable response 与 stream item 不能由 custom Codec 返回 null；违反现在是 `DataLoss`。
- Codec 必须完整消费 payload，拒绝非规范 null、整数、UTF-8 和尾随字节。
- Client/Server interceptor 的 `next` 只能调用一次并必须被等待；响应调用不能静默不调用 `next`。
- `[NonCancellable]` 明确表示服务业务不接收 token；调用方取消不保证业务停止。
- Shutdown、resolver、hosted service、transport 与动态模块 cleanup 的异常会被保留和观察，不再静默吞掉 sibling failure。

## 文档与包

- 发布源码所有公开 API 由 CS1591 gate 强制 XML 注释。
- 每个运行时 NuGet 包包含与主程序集同名的 XML IntelliSense 文件。
- 旧 `audit-*`、`migration-0.x.*`、`performance-0.x.*` 是开发过程证据，不是 2.0 用户契约，已由当前主题文档、CHANGELOG、测试和最终性能基线替代。

## 升级清单

1. 统一 SDK、Generator、Abstractions、Runtime、Client、Server、Hosting 和 serializer adapter 为 2.0；同一进程不混装 1.1.x。
2. 清理所有契约、服务和插件项目的旧 `bin/obj`，重新生成 API 5，并把 Generator diagnostics 当错误处理。
3. 为所有没有 token 的 RPC 显式确认 `[NonCancellable]` 是否合理。
4. 验证 DTO field id、required/nullability 和 custom Codec wire identity。
5. 验证 TLS、authentication、authorization、metadata 与错误消息不泄露敏感数据。
6. 验证 Unary、OneWay、三类 Streaming、deadline、取消、断连和 Server Stop。
7. 若使用 topology/resilience，验证 generation churn、last-good、retry deadline 和 breaker。
8. 若使用动态模块，验证替换期间旧调用排空与 ALC 最终回收。
9. 对实际发布入口执行包含五种调用形态的 NativeAOT smoke（若适用）、PackageSmoke 和固定负载基线。

Protocol v2 的当前 wire 定义见 [protocol-v2.md](protocol-v2.md)。Generated ABI（API 5）与 Protocol v2 是独立版本轴；迁移到 2.0 不改变 wire frame 或 capability negotiation。
