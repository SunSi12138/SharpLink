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

以下数字来自原提案的历史微基准，尚未针对当前合并后的 3.0 候选重新验证。
在固定核上对当时的真实 generated stub 实测：`TryGetMethodDescriptor` 单次 38.3 ns，而同一批 key 的
`bool` switch 只要 6.3 ns。JIT asm 显示每个 case 都会 `call` 到十参数的
`RpcMethodDescriptor` 构造函数（六个参数走栈，其中包含 16 字节 `Nullable<TimeSpan>`），IL 显示
该构造函数又把 `init` 自动属性写成访问器调用。这些历史结果支持避免无条件 descriptor 物化，但不能证明当前表示最优或推导端到端收益。

同一批事实还被生成/声明了四次（manifest descriptor、descriptor 表、cancellation 表、invoke
switch），而且两个默认实现语义相反：`TryGetMethodDescriptor` 默认 `false`、
`SupportsCancellation` 默认 `true`，于是"methodId 不存在"和"这个 stub 没有事实表"无法区分。

## Decision

1. 引入 `RpcMethodShape`：把 resolution 状态、invocation kind、client-stream 数量、取消支持、
   response payload/nullability、method timeout 是否存在、idempotency 打包进
   一个 32 位值类型。表示和位布局保持私有，公开构造器验证类型化事实。
   `default` 是无效值，调度边界拒绝它，不提供缺失事实的兼容回退。
2. `IRpcStub` 只保留一个解析入口 `RpcMethodShape ResolveMethodShape(long methodHash)`；契约外的
   id 返回 `UnknownMethod`。该入口是必需成员，手写 stub 也必须实现。`TryGetMethodDescriptor` 与 `SupportsCancellation` 被移除。
3. 服务端在 service 路由成功后解析一次，并把 `ResolvedMethodCall` 作为 resolved call state 传给
   admission、取消判定、stream 预留、telemetry 与 invocation orchestration；错误路径复用同一个值。排队恢复以原 registration 为准；outbound stream pump 在等待前捕获原方法事实，错误映射不重新查询当前服务注册表。
4. `RpcMethodDescriptor` 在服务端作为冷路径可观测性投影（客户端仍用于调用控制和重试）：构造函数直接写只读字段，`FromShape` 负责投影，
   `IRpcStub.DescribeMethod(long, RpcMethodShape, out RpcMethodDescriptor)` 让 generated stub 附加
   自己声明的 timeout。只有创建 interceptor / exception-mapper context 时才会投影不可变标量 snapshot；context 不持有 generated stub。无 observer 的普通路径不物化 descriptor。
5. 服务端 telemetry 从 `(contractId, methodId, shape)` 启动，`CallScope` 不再保存 descriptor。
6. generator 每个契约发射由类型化构造器初始化的只读 shape 字段及解析 switch，不再发射 descriptor 表与重复的 cancellation 表。client-stream count 使用 7 位，支持既有 127 上限；timeout 在冷 DescribeMethod 路径按 methodHash 查询，不使用会溢出的 timeout ordinal。

## Consequences

以下性能和尺寸数字仅描述原提案；当前类型化字段、32 字节 descriptor 和合并后的 ABI 需要新测量。

- 默认 unary/OneWay 路径由"descriptor 查询 + cancellation 查询"变为一次 packed 解析：实测
  39.8 ns → 15.2 ns（含读取所需事实），单次解析入口 38.3 ns → 11.8 ns。
- 解析表 IL 由 14 259 B 降到 7 775 B（−45%），128 方法契约的 stub 文件缩小 8.5%。
- 3.x 破坏性变更：`IRpcStub` 成员变更、`RpcMethodDescriptor` 失去 `init` 访问器、
  `SharpLinkGeneratedMethodDescriptor` 以 shape 取代 `Kind`/`SupportsCancellation`、
  `SharpLinkServerInvocationContext` 新增 `Shape`。
- **generated ABI 版本在此 3.0 发布边界上从 API 4 提升到 API 5**（`SharpLinkGeneratedManifestVersions.Api`
  与 `AbiIdentity`、generator 的 `ApiVersion`/`GeneratedAbiIdentity`、`eng/release-versions.json`），
  旧 2.0 生成程序集必须在构造前被拒绝。该提升及 3.0 公共 API baseline 的重建随此变更一并完成。
- `RpcMethodShape` 当前为定宽值；私有位布局可演进，公开事实或生成契约变化仍需兼容性审查。
- 四个 `Invoke*` 入口与线协议保持不变；调用入口的去折叠属于独立议题。
- 如果后续需要进一步压低解析成本，把 `ResolveMethodShape` 的返回类型从 4 字节结构改为
  `uint` 可再省约 3 ns（实测 11.9 ns → 8.5 ns），代价是失去类型安全；当前选择保留强类型。

## Alternatives considered

- **只缓存 `RpcMethodDescriptor`（原候选 2）**：默认路径只能省掉 `SupportsCancellation`
  （6.4 ns），却要为携带取消事实让 descriptor 每次解析多花 5.5 ns；实测净收益为负。放弃。
- **把 descriptor 表改为直接写字段的普通结构体**：让 41.6 ns 降到 26.7 ns，但热路径仍要物化
  40 字节；收益远小于不物化。作为 `RpcMethodDescriptor` 自身的改进被吸收进本次决定。
- **registration 所有的不可变 method fact 表**：可与 dynamic module 和 #799 owner-bound factory 共存，
  并非天然违反所有权。它增加注册工作、内存与生命周期规则，目前缺少足够收益证据，因此不是本次合并前提。
  后续若验证内部 ordinal 能省掉第二次 hash dispatch，可在原 registration 内实验；不引入全局 delegate 表或线协议 ordinal。
- **线协议携带 method 序号以数组索引代替 switch**：只剩 3–4 ns 收益，却要引入协议版本协商与
  "序号与 hash 不一致"这一新失败模式。放弃。

## Validation

- `test/SharpLink.UnitTests/Abstractions/RpcMethodShapeTests.cs`：类型化边界、无效默认值、未知方法和投影语义。
- `test/SharpLink.UnitTests/Server/MethodFactResolutionCountTests.cs`：断言每个 two-way 与 OneWay
  RPC 只解析一次，且没有消费者时不投影 descriptor。
- 生成代码断言：`test/SharpLink.Generator.Tests/RpcAnalyzerGeneratedArtifactsTests.cs` 要求
  `ResolveMethodShape` 存在、`TryGetMethodDescriptor`/`SupportsCancellation` 不再发射。
- 复现命令见 PR 描述与仓库 `eng/` 脚本；JIT micro、生成 IL 与 NativeAOT 尺寸证据记录在 PR 中。

## Combined unreleased 3.0 contract

The exact neutral identity is `sharplink-3.0-api5-v1`; numeric API remains 5. This is the proposed combined method-shape and owner-bound UnsafeBlit factory contract for the unreleased 3.0 boundary. Intermediate validation artifacts are not supported compatibility targets; rebuild all generated artifacts together before promotion. Both earlier feature-specific API5 identities are rejected before manifest construction. The identity is not permanent: future incompatible generated contracts must receive a different exact identity. Package version remains 3.0.0 and wire protocol is unchanged.
