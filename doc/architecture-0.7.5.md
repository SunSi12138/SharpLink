# SharpLink 0.7.5 静态多端点设计

本文档是 0.7.5 的实现设计。它补充 `architecture.md`，在 0.7.5 全部验收前不描述 0.7.6 的动态 Resolver、0.7.7 的 Retry 或后续韧性策略。

## 目标与非目标

- 保留既有 `UseTransport` 和所有 `UseTcp`、`UseUds`、`UseNamedPipe`、`UseAnonymousPipe`、`UseSharedMemory` 路径的固定单端点快路径。
- 通过 `UseEndpoint` 或 `UseEndpoints` 显式启用静态 endpoint 配置；单个 endpoint 在 `Build()` 时折叠为固定快路径，两个或更多 endpoint 才构造 Cluster 状态。
- 一个 Cluster Client 继续只拥有一套 proxy、manifest、interceptor、runtime context 和调用管线。endpoint 不是子 Client。
- 动态成员变更、Resolver、Retry、Admission、Circuit Breaker 和新增 wire capability 均不在 0.7.5 范围内。

## 公共模型

`SharpLink.Abstractions` 定义不可变的 `SharpLinkTransportAddress` 记录层次、`SharpLinkEndpoint`、endpoint transport factory 委托和 selector SPI。地址和值对象在构造时验证自身不变量；endpoint 的 ID、属性个数和属性键值长度在 Builder/Cluster snapshot 边界验证。

endpoint 进入 Client 时将被深复制：`Id`、`Address` 和 `Authority` 是不可变值，Attributes 复制为只读字典。Client 不保留调用方提供的 collection 或 dictionary 引用。`Address + Authority` 是连接 generation 的身份；0.7.5 的静态拓扑只有 generation `1`，但候选和诊断保留 generation 字段，使该事实不泄漏到调用路径。

`SharpLinkEndpointSelectionContext` 为只读 `ref struct`，只携带当前不可变 Ready candidate snapshot、数量和 `ulong` exclusion mask。selector 返回该 snapshot 的索引；越界、被排除或失去 Ready 状态的索引使当前调用以 `FailedPrecondition` 失败，selector 异常只失败当前调用。一个候选时不调用随机数或用户 selector。

## Builder 运行模式

Builder 在 `Build()` 时一次性冻结为下列模式之一：

```text
FixedTransport
  UseTransport / legacy transport helpers
  UseEndpoint(s) with exactly one endpoint

StaticEndpoints
  UseEndpoint(s) with two to 64 endpoints
```

`UseTransport` 与 `UseEndpoint(s)` 互斥。固定模式允许 `UseConnectionPool`，不允许 `UseCluster`；集群模式要求 `UseCluster` 或使用其默认快照，不允许 `UseConnectionPool`。Cluster 的连接总量和 endpoint 内连接数分别由 `MaxConnections`、`MaxConnectionsPerEndpoint` 约束；Connecting 和 Ready 都计入总预算，retiring 连接使用独立预算。

每次调用 endpoint transport factory 仅由 Client 所有。每个 factory 只会在该 endpoint 的停止/清理路径调用一次 `DisposeAsync`。内置 factory helper 位于 `SharpLink.Client`，将 TCP、UDS、NamedPipe 和 SharedMemory 地址映射到现有 transport；AnonymousPipe 的一次性 handle offer 不支持内置多 endpoint 配置。

## Cluster 状态机与连接所有权

Static Cluster 的 Client 拥有一个 `StaticClusterRuntime`：冻结的 `EndpointState[]`、不可变 Ready candidate 数组、全局连接 reservation、retiring connection 预算、一次性 topology signal 和已跟踪 worker 集合。每个 `EndpointState` 拥有一个 endpoint generation 的 factory、Ready connection snapshot、Connecting/Ready/Draining 计数、active-call 计数、reconnect 信号和该 endpoint 的 worker。

连接建立采用 endpoint-first：在全局预算内先尝试让各 endpoint 各有一条连接，之后才扩展同一 endpoint。最多四个内部连接操作并发。`ConnectAsync` 在任意 endpoint 完成 RPC handshake 后成功；后台继续填充实际 `min(MinReadyEndpoints, endpointCount)`。任一 endpoint 的失败只启动自身退避重连，不阻塞其余 Ready endpoint。Stop 获胜后取消所有 endpoint worker，等待其退出，释放 connection 与 factory，且不会再建立连接。

Ready candidate snapshot 只包含至少一条 Ready connection 的 endpoint。它仅在启动完成、连接数在零与非零间转换、或 endpoint 固定成员初始化时重建，并以 `Volatile.Write` 作为同一 endpoint/candidate 对发布。调用路径只读取这一快照与原子计数，不取得 topology writer lock，也不重建数组。Ready/active 计数通过 endpoint-owned provider 实时读取，因此连接扩缩容或 in-flight call 变化不会迫使快照重建。

## 选择与调用

调用先从 Cluster Ready candidate snapshot 选择 endpoint，再用该 endpoint 内已有的连接 P2C 选择 connection。P2C 用两个不同候选的 `ActiveCallCount / ReadyConnectionCount` 作 64 位交叉相乘比较；Random、RoundRobin 和 LeastPending 也只在静态 snapshot 上运行。RoundRobin cursor 是 Client 实例字段，LeastPending 使用旋转起点消除固定并列偏差。

endpoint 在选择后失去最后一个 Ready connection 时，该调用在同一 snapshot 上设置 exclusion bit 并重新选择，最多候选数次；无需 `HashSet`。所有 endpoint 不可用时保留既有 `WaitForReady`、deadline、cancellation 与 Stop 语义。Streaming/OneWay 在开始时选择一个 connection 后一直绑定它；GoAway 仅排空所属 endpoint 中的对应 connection。Retiring connection 不消耗 Ready/Connecting budget，最多保留 `MaxRetiringConnections` 条；超出该独立预算的连接会被关闭并让当前调用得到连接关闭结果。

## 验收映射

- 公共 address/endpoint/selector/options API 由 Unit 与 PackageSmoke 覆盖。
- Builder 模式、冻结和 factory ownership 由 Unit 覆盖。
- P2C、Random、RoundRobin、LeastPending 与 selector failure 由无网络 Unit 覆盖。
- TCP、UDS、NamedPipe、SharedMemory 多 endpoint 分布、局部故障、独立重连、GoAway、Stop/Dispose 并发由 Integration 覆盖。
- fixed single 与 one-static-endpoint 继续使用原路径；性能结论只在完整 0.7.5 矩阵完成后写入性能报告。
