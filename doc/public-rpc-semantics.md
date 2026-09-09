# Public RPC semantics

本文是 SharpLink 公开 RPC 行为的 **code-review contract**。目标不是重复 API 参考，而是固定最容易被实现细节悄悄改变的边界。若实现、测试与本文出现冲突，应先判断公开语义是否真的要变化；语义变化必须在同一 PR 中更新测试和本文。

## Review quick reference

| Concern | Public semantic boundary |
| --- | --- |
| Deadline | 在逻辑调用创建时冻结为单调时钟 deadline；同一个 deadline 约束后续调用阶段和所有 retry，不会在重试、重选 endpoint 或 stream 启动后重新计时。此前的 `ConnectAsync`、transport dial、handshake、readiness wait 不属于这个 RPC deadline。 |
| `ConnectAsync` | 启动或加入 client 自己的连接生命周期；它不是“所有 endpoint 已 Ready”的屏障。需要 N 个 Ready endpoint 时使用 `WaitForReadinessAsync(N)`。 |
| `[OneWay]` | 成功只承诺本地发送边界成功，不承诺远端 handler 已执行，更不承诺业务成功。需要确认远端执行结果时使用 request/response RPC。 |
| Retry | 只可能发生在 `[Idempotent]` Unary；OneWay/Streaming 不 retry。每次 retry 是同一逻辑调用的新物理 attempt，并重新执行 endpoint selection，但仍共享原 logical deadline。timeout/disconnect 不证明 Server 未执行过请求。 |
| Multi-cluster replace | `ReplaceClusterAsync` 前已经取得的 proxy 固定绑定旧 child；要调用 replacement child，需要重新 `Get<T>()`。 |
| Server module / endpoint update | Server-side service/module generation replacement、resolver topology 和 endpoint-selection policy 更新不会要求重取普通 client proxy；但已经开始的 call / physical attempt 不会中途迁移到新 generation 或 endpoint。 |

## 1. Deadline、timeout 与 cancellation

### 1.1 Deadline 从哪里开始

Client 在**逻辑调用创建边界**解析 timeout，并用配置的 `TimeProvider` 单调时间生成绝对 `RpcDeadline`。它不是在真正 socket write、server admission 或第一次 retry 时才开始。

Timeout 来源按以下规则组合：

- 方法显式 `[Timeout(...)]` 优先于 client fallback。
- Unary 默认可以使用 `UseRequestTimeout(...)` 的 client fallback。
- OneWay 和三类 Streaming **不会自动继承 client fallback timeout**；要给这些调用固定 timeout，应在方法上使用 `[Timeout]`（无参数 `[Timeout]` 明确要求使用 client fallback）。
- 嵌套/ambient SharpLink 调用会继承父调用 deadline，而且子调用不能把父 deadline 延长。不同 `TimeProvider` 之间只投影“剩余时间”，不会制造额外预算。

`UseRequestTimeout()` 提供的是 client-wide Unary fallback，不应被当成业务 SLO 的替代品。需要更具体的服务/方法预算时，应使用方法 timeout 或让上游调用 lifetime 继续向下游传播。

### 1.2 一个 deadline 覆盖哪些阶段

只要某阶段属于同一个逻辑调用，它就不会获得新的 timeout 窗口。当前实现用同一个 logical deadline 约束：

- client interceptor 继续执行之前的 progress check；
- endpoint selection / endpoint admission 以及 admission 返回的 retry-after 等待；
- 选择等待型 pending-slot API 时的 slot wait；
- deadline-bearing request 的 SendPump emission；
- Unary response wait；
- 有 deadline 的 stream lifetime / producer progress；
- retry decision、backoff/jitter 和后续所有 attempts。

两个容易误读的本地容量边界：

- 标准 generated Unary 当前使用有界 pending table 的**立即租用**。pending capacity 已满时会本地 `ResourceExhausted(PendingRequestCapacity)`，并不会默认排队等 slot。只有明确选择 wait-for-slot 的内部/扩展路径才存在 pending-slot wait；这种 wait 仍受同一个 deadline 约束。
- Request 进入 session send queue 也不是一个隐含的无限等待点。标准 request enqueue 在 send queue 满时会本地 `ResourceExhausted(SendQueueCapacity)`；如果 deadline-bearing Request 已经成功入队，它的实际 emission/flush 仍必须在原 deadline 内完成。

服务端收到带 TimeBudget 的请求后，admission queue 也受该调用 budget/cancellation 约束。Client 自己的绝对 deadline 同时继续运行，所以网络发送、传输和响应等待不会因为跨进程 TimeBudget 重新开始而延长 caller 的总预算。

### 1.3 `ConnectAsync`、dial、handshake、readiness 不属于 RPC deadline

RPC deadline 只在 RPC logical call 创建时出现。因此下面这些**此前的启动/连接阶段不会被后续 RPC timeout 追溯计费**：

- `ConnectAsync`；
- transport dial；
- protocol/TLS handshake；
- `WaitForReadinessAsync`。

它们有各自的生命周期与取消边界：`ConnectAsync` 的 caller token 只取消当前 caller 的等待，共享的 client-owned connection attempt 可以继续；`WaitForReadinessAsync` 的 token 取消 readiness wait；SharpLink RPC handshake 由独立的 `SharpLinkProtocolOptions.HandshakeTimeout` 约束，启用 TLS 的 transport 还可能有独立的 TLS handshake timeout。transport dial 服从 transport/client lifecycle 的取消机制，而不是某个尚未创建的 RPC deadline。

应用当然可以用同一个外部 `CancellationToken` 或更高层 orchestrator 同时限制“启动 + readiness + RPC”的总时间，但那是应用级 budget，不会把 SharpLink 的 RPC deadline 改造成 connect timeout。

### 1.4 emission 时的 TimeBudget

Request 不会在逻辑调用刚创建时把一个静态 timeout 数字永久写进 wire。deadline-bearing Request 保留本地绝对 deadline，SendPump 在 transport emission 的最后边界重新计算剩余时间，然后写入 `TimeBudget` 并 flush。若在 emission 前预算已经耗尽，请求本地失败为 `DeadlineExceeded`，不会把过期 Request 发布到 transport。

### 1.5 terminal precedence

不要依赖“多个终止条件完全同时发生”时的未承诺调度顺序。可以依赖的是：

- caller `CancellationToken` 被取消时，调用以 `OperationCanceledException` 结束，并且 cancellation 不进入 retry；
- deadline 获胜时以 `SharpLinkException` + `SharpLinkErrorCode.DeadlineExceeded` 结束；绝对 deadline 已经过期后到达的 late response 不能把调用复活成成功；
- client stop/drain 导致的 client-owned wait 在 caller cancellation 未先获胜时以 `ConnectionClosed` 等生命周期错误结束；shutdown 不会延长已有 deadline；
- retry/backoff 每个 progress boundary 都重新检查原 deadline，因此不存在“最后一次 retry 获得完整新 timeout”的行为。

## 2. `ConnectAsync` 与 readiness 不是同一件事

`ConnectAsync` 建立的是 client 的 topology-specific connectivity boundary，不是 topology-wide readiness barrier。

- 单 endpoint client：初始 connect 成功意味着配置的 `MinConnections` 已建立并可以发布 Ready。
- static multi-endpoint / dynamic resolver client：`ConnectAsync` 启动或加入该 topology 的 connectivity lifecycle；它不会等待每个目标 endpoint 都 Ready。Dynamic resolver 的已接受空 snapshot 也不会被解释成“等待所有未来 endpoint Ready”。
- 业务启动必须要求至少 N 个 Ready endpoint 时，使用 `WaitForReadinessAsync(N, token)`。它会先启动/加入 `ConnectAsync`，然后等待 readiness snapshot 满足 `State == Ready`、至少一个 Ready connection、并且 `ReadyEndpoints >= N`。
- `GetReadinessSnapshot()` 是 level-triggered 观察，不是 lease。它在读取后可以立即因为断连、resolver 更新或 drain 失效。
- `WaitForReadinessAsync` 不会提高连接池目标或创造额外容量；它只等待现有 topology/convergence policy 达到条件。Dynamic resolver 可以跨当前空/较小 snapshot 等待未来 topology 更新。

因此 code review 中不要把 `await ConnectAsync()` 改写成“所有 endpoints 已可接流量”，也不要把 readiness wait 当成永久稳定性保证。

## 3. `[OneWay]` 成功到底承诺什么

OneWay 没有 response frame，因此不存在“远端业务成功”这一客户端可观察结果。

当前本地成功边界分两种：

- **普通、无 deadline 的非 streaming OneWay**：调用成功表示 Request 已被本地 `RpcSession` 的 SendPump 接收/入队。若本地 send queue 满，会立即得到本地 `ResourceExhausted`。返回成功时数据可能尚未完成 transport flush。
- **带 deadline 的 OneWay**：为了保证过期 Request 不被发布，客户端会观察该 Request 所在 batch 的 emission；只有 `PipeWriter.FlushAsync` 成功后该发送阶段才成功。若 deadline 在 emission 前耗尽，则本地 `DeadlineExceeded`。

这两种成功都**不表示**：

- server 已收到 Request；
- server admission 已接受；
- handler 已开始或完成；
- 业务状态已经提交。

服务端 OneWay 在 admission overload 下还可能按配置直接丢弃；客户端因为没有 response 无法从返回值判断这一结果。

带 client stream 的 OneWay 还需要完成本地 stream producer 生命周期；其初始 Request 必须先通过 emission deadline 检查，producer 才会启动。这仍然不是远端业务确认。

如果业务必须确认“远端 handler 已成功完成”或“副作用已经提交”，应使用有响应的 RPC，或在应用协议中设计明确 acknowledgement / idempotency mechanism，而不是把 `await OneWay` 当成远端确认。

## 4. Replacement 后 proxy 与 in-flight call 如何绑定

Replacement 不能用一条“proxy 会/不会自动 rebind”覆盖所有类型；binding boundary 取决于替换发生在哪一层。

### 4.1 Multi-cluster child replacement

Coordinator 的 `ReplaceClusterAsync` 先把 candidate child connect/validate 到可发布状态，再原子切换 slot，最后排空旧 child。

- publication **之后**的新 `Get<T>()` 绑定新 child；
- publication **之前**已经取得的 proxy 仍绑定旧 child，不会自动 rebind；
- 已经开始的 in-flight call 不迁移到新 child；它按旧 child 的连接/排空/取消生命周期完成或失败；
- 旧 proxy 在旧 child 进入停止阶段后发起的新调用会看到旧 child 的 lifecycle rejection，而不是偷偷转发到新 child。

因此要使用 replacement child，应用必须重新 `Get<T>()`。这个设计刻意避免每次 proxy 调用都回到 coordinator 做热路径 lookup。

### 4.2 Server-side dynamic service/module generation replacement

Server-side dynamic service/module replacement 的 publication boundary 与 multi-cluster proxy binding 不同：

- 已经开始的调用继续使用其捕获的旧 server generation/service/codec；
- replacement publication 之后到达 Server 的新调用使用新 generation；
- 旧 generation 在 drain 完成后才可卸载；
- 普通 client proxy 只绑定自己的 client/channel，并不持有某个 server service object，因此仅仅因为 Server 发布了新 service/module generation，**不需要重新 `Get<T>()`**。

如果替换的是 **client-side collectible contract assembly** 本身，则旧 proxy/type/codec 仍属于旧 AssemblyLoadContext；要使用新 contract assembly 并允许旧 ALC 卸载，必须释放旧 proxy/type 等强引用，并从新 contract generation 获取新的 proxy。这是 client module ownership 问题，不是 server service rebind。

### 4.3 Dynamic endpoint topology / selection policy

Endpoint resolver 或 runtime selection-policy 更新不会重绑 proxy；proxy 仍属于同一个 client。每个**物理 attempt**捕获自己的 endpoint/topology/selection 边界，已选中的 in-flight attempt 不会在中途搬到另一个 endpoint。后续 retry 是新的 attempt，因此可以看到更新后的 topology/policy 并重新执行 endpoint selection；它仍可能因为当前 Ready set/policy 选择到与前一 attempt 相同的 endpoint。

因此 resolver/selection-policy 更新通常由已有 proxy 的**后续新 attempt**透明观察，不要求重新 `Get<T>()`。

## 5. Retry 的公开边界

Retry 默认关闭。即使启用，也只有同时满足以下条件的调用才进入 retry pipeline：

1. RPC shape 是 Unary；
2. method 标注 `[Idempotent]`。

OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 都不 retry。自定义 `ISharpLinkRetryPolicy` 可以改变**符合资格的 Unary**对失败的决策，但不能把非幂等调用或 Streaming/OneWay 变成可 retry 调用。

默认 retryable error 是 `Unavailable` 和 `ConnectionClosed`。`MaxAttempts` 是**总 attempts 数，包含首次**。

每个 logical call 在开始时捕获 retry-policy generation，并共享同一个 logical deadline。每次 retry：

- 重新执行 endpoint selection；
- 使用原 logical call 的剩余 deadline；
- delay 取 policy/backoff 与 endpoint admission `RetryAfter` 中需要等待的边界；
- delay 前后都检查 caller cancellation、client shutdown 和 deadline；
- deadline 一旦耗尽即以 `DeadlineExceeded` 终止，不再开始下一个 attempt。

因此不要把 retry 配置理解成 `MaxAttempts × RequestTimeout`。总时间预算始终是一个 logical deadline。

更重要的是：**Client 看到 timeout、connection loss 或 response 丢失，不证明 Server 没有执行过该 Request。** Request 可能已经到达 Server，handler 甚至可能已经提交副作用，只是 Client 没有观察到成功 response。因此：

- `[Idempotent]` 是“业务允许重复执行”的声明，不是“上一 attempt 一定没执行”的证明；
- automatic retry 只应启用于确实可安全重复的操作；
- 对非天然幂等的写操作，应使用业务 idempotency key、去重/事务设计或显式 recovery protocol，而不是依赖 disconnect/timeout 来判断“可以安全重发”。

## 6. 容易混淆的相邻边界

### `ResourceExhausted`：先看 `DetailCode`

同一个顶层 `SharpLinkErrorCode.ResourceExhausted` 可以来自不同层。公开的 `SharpLinkErrorDetails.ResourceExhausted.*` 提供稳定 machine-readable detail code。例如：

- client-local `PendingRequestCapacity`：pending table 已满；
- client-local `SendQueueCapacity`：session send queue 已满；
- server-side `ServerCallCapacity` / `PerConnectionCallCapacity` / `AdmissionQueue` / `AdmissionRate` 等：远端容量或 admission rejection。

不要仅靠错误 message 文本判断来源；review/metrics 应优先看 `Code + DetailCode`。

### Streaming startup、acknowledgement 与 lifetime

Streaming 不自动获得 Unary 的 client fallback timeout；但一旦它有方法 deadline 或继承的 ambient deadline，这个 deadline 属于**整个 logical stream call**，不会在 request startup 完成、第一次 `MoveNextAsync` 或 producer 启动后重置。已开始的 stream 也不会因为 reconnect/retry 跨连接迁移。

对 ServerStreaming / Duplex，初始 `Response` 只是服务端对 stream request 的 acknowledgement，不是整个 stream 的 terminal success；真正的正常终止由 `StreamComplete` 驱动。业务不能把“stream 已开始”解释成“所有 item 已成功完成”。

### Shutdown / drain 与 cancellation

Server 的 graceful `StopAsync(gracefulTimeout)` 会关闭新 call admission，并给已经开始的调用一个受 `gracefulTimeout` 限制的排空窗口；这个窗口不是新的 RPC deadline，已有 caller cancellation/deadline 仍继续生效。

Client `StopAsync` 是不同语义：它停止 reconnect、拒绝新调用、失败 pending work 并释放 client-owned resources；不要把它理解成“等待所有 in-flight RPC 自然完成”的 server-style graceful drain。无论哪一侧 shutdown，都不能把已经 terminal 的 call 恢复成成功。

## 7. Implementation and test evidence

重要语义都有现有自动化证据；修改这些边界时应同时检查对应测试：

| Semantic | Evidence |
| --- | --- |
| logical deadline 不重置、stream deadline | `test/SharpLink.UnitTests/Client/SharpLinkClientLogicalDeadlineTests.cs`, `ClientStreamProducerDeadlineTests.cs` |
| TimeBudget 在 emission 边界采样、过期 request 不发送 | `SharpLinkClientTimeBudgetTests.cs`, `SharpLinkClientTrackedEmissionDeadlineTests.cs`, `SharpLinkClientOneWayTimeBudgetTests.cs` |
| timeout/cancellation | `SharpLinkClientCallOptionsTests.cs`, `SharpLinkClientTimeoutTests.cs`, `SharpLinkClientCancellationTests.cs` |
| connect / readiness snapshot / wait | `SharpLinkClientLifecycleStartStopTests.cs`, `SharpLinkClientReadinessStateTests.cs`, `SharpLinkClientReadinessWaitTests.cs`, `SharpLinkClientReadinessPublicationTests.cs` |
| retry eligibility、endpoint reselection、deadline | `SharpLinkClientRetryBehaviorTests.cs`, `SharpLinkClientRetryDeadlineTests.cs`, `SharpLinkClientRuntimeRetryPolicyTests.cs` |
| multi-cluster replace / rollback / proxy binding | `SharpLinkMultiClusterMutationTests.cs`, `SharpLinkMultiClusterMutationConcurrencyTests.cs`, `RuntimeMultiClusterIntegrationTests.cs` |
| dynamic module generation replacement / drain / unload | `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.cs` |
| runtime endpoint policy 与 attempt capture | `EndpointSelectionRuntimeInteractionTests.cs`, `EndpointSelectionRuntimeTests.cs` |

可运行示例：`demo/Timeout`, `demo/Cancel`, `demo/Oneway`, `demo/Resilience`, `demo/MultiCluster`。

更完整的主题文档见 [调用、流式与取消](calls-and-streaming.md)、[服务发现与韧性](resilience.md)、[Server admission](admission-control.md) 和 [多集群与动态模块](dynamic-modules-and-multicluster.md)。
