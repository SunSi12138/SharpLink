# ADR 0002: Generated method facts are resolved once per RPC as one packed shape

- Status: Proposed
- Date: 2026-09-25
- Related: #732, #728

## Context

服务端每个 RPC 会把同一组 generated method facts 解析 2–5 次：

- `IRpcStub.SupportsCancellation(long)`：一次 64 位 switch；
- `IRpcStub.TryGetMethodDescriptor(long, out RpcMethodDescriptor)`：一次 64 位 switch 加一次
  40 字节 record struct 物化。它被无条件调用以喂 telemetry，即使没有任何 ActivityListener、
  也没有 metrics 订阅；
- `CreateCallContext` 在同一个方法体内再查一次 descriptor、dynamic module 分支再查一次、
  OneWay admission 排队恢复路径重入时再查一次。

在固定核上对真实 generated stub 实测：`TryGetMethodDescriptor` 单次 38.3 ns，而同一批 key 的
`bool` switch 只要 6.3 ns。JIT asm 显示每个 case 都会 `call` 到十参数的
`RpcMethodDescriptor` 构造函数（六个参数走栈，其中包含 16 字节 `Nullable<TimeSpan>`），IL 显示
该构造函数又把 `init` 自动属性写成访问器调用。**瓶颈从来不是 method-id switch，而是 descriptor
的物化与 ABI 搬运。**

同一批事实还被生成/声明了四次（manifest descriptor、descriptor 表、cancellation 表、invoke
switch），而且两个默认实现语义相反：`TryGetMethodDescriptor` 默认 `false`、
`SupportsCancellation` 默认 `true`，于是"methodId 不存在"和"这个 stub 没有事实表"无法区分。

## Decision

1. 引入 `RpcMethodShape`：把 resolution 状态、invocation kind、client-stream 数量、取消支持、
   response payload/nullability、method timeout 是否存在、idempotency 与 timeout 序号打包进
   一个 32 位值类型。它的 `default` 是保守的 `Unresolvable`（报告支持取消、stream 数量未知），
   因此零初始化不会静默降级取消语义。
2. `IRpcStub` 只保留一个解析入口 `RpcMethodShape ResolveMethodShape(long methodHash)`；契约外的
   id 返回 `UnknownMethod`。`TryGetMethodDescriptor` 与 `SupportsCancellation` 被移除。
3. 服务端在 service 路由成功后解析一次，并把 `ResolvedMethodCall` 作为 resolved call state 传给
   admission、取消判定、stream 预留、telemetry 与 invocation orchestration；错误路径复用同一个值。
4. `RpcMethodDescriptor` 降级为可观测性投影：构造函数直接写只读字段，`FromShape` 负责投影，
   `IRpcStub.DescribeMethod(long, RpcMethodShape, out RpcMethodDescriptor)` 让 generated stub 附加
   自己声明的 timeout。只有真正的消费者（注册了 interceptor 并读取
   `SharpLinkServerInvocationContext.Method`）才会触发投影。
5. 服务端 telemetry 从 `(contractId, methodId, shape)` 启动，`CallScope` 不再保存 descriptor。
6. generator 每个契约只发射一张 packed shape 表，不再发射 descriptor 表与重复的 cancellation 表。

## Consequences

- 默认 unary/OneWay 路径由"descriptor 查询 + cancellation 查询"变为一次 packed 解析：实测
  39.8 ns → 15.2 ns（含读取所需事实），单次解析入口 38.3 ns → 11.8 ns。
- 解析表 IL 由 14 259 B 降到 7 775 B（−45%），128 方法契约的 stub 文件缩小 8.5%。
- 3.x 破坏性变更：`IRpcStub` 成员变更、`RpcMethodDescriptor` 失去 `init` 访问器、
  `SharpLinkGeneratedMethodDescriptor` 以 shape 取代 `Kind`/`SupportsCancellation`、
  `SharpLinkServerInvocationContext` 新增 `Shape`。
- **generated ABI 版本必须在 3.0 发布边界上从 API 4 提升到 API 5**（`SharpLinkGeneratedManifestVersions.Api`
  与 `AbiIdentity`、generator 的 `ApiVersion`/`GeneratedAbiIdentity`、`eng/release-versions.json`），
  否则 2.0 生成的程序集会被 3.0 runtime 接受并按保守默认值运行（OneWay 直接断连、streaming 不预留
  stream），而不是被拒绝。该提升与公共 API baseline 的重建属于发布边界动作，不在本 ADR 的实现范围内。
- `RpcMethodShape` 是定宽值；未来新增事实应使用保留位，而不是新增成员。
- 四个 `Invoke*` 入口与线协议保持不变；调用入口的去折叠属于独立议题。
- 如果后续需要进一步压低解析成本，把 `ResolveMethodShape` 的返回类型从 4 字节结构改为
  `uint` 可再省约 3 ns（实测 11.9 ns → 8.5 ns），代价是失去类型安全；当前选择保留强类型。

## Alternatives considered

- **只缓存 `RpcMethodDescriptor`（原候选 2）**：默认路径只能省掉 `SupportsCancellation`
  （6.4 ns），却要为携带取消事实让 descriptor 每次解析多花 5.5 ns；实测净收益为负。放弃。
- **把 descriptor 表改为直接写字段的普通结构体**：让 41.6 ns 降到 26.7 ns，但热路径仍要物化
  40 字节；收益远小于不物化。作为 `RpcMethodDescriptor` 自身的改进被吸收进本次决定。
- **build 阶段建立 method fact 表（原候选 4）**：与 dynamic module、unknown method 与 per-stub
  所有权语义冲突，且收益不超过 packed shape。放弃。
- **线协议携带 method 序号以数组索引代替 switch**：只剩 3–4 ns 收益，却要引入协议版本协商与
  "序号与 hash 不一致"这一新失败模式。放弃。

## Validation

- `test/SharpLink.UnitTests/Abstractions/RpcMethodShapeTests.cs`：打包/解包、保守默认值、投影语义。
- `test/SharpLink.UnitTests/Server/MethodFactResolutionCountTests.cs`：断言每个 two-way 与 OneWay
  RPC 只解析一次，且没有消费者时不投影 descriptor。
- 生成代码断言：`test/SharpLink.Generator.Tests/RpcAnalyzerGeneratedArtifactsTests.cs` 要求
  `ResolveMethodShape` 存在、`TryGetMethodDescriptor`/`SupportsCancellation` 不再发射。
- 复现命令见 PR 描述与仓库 `eng/` 脚本；JIT micro、生成 IL 与 NativeAOT 尺寸证据记录在 PR 中。
