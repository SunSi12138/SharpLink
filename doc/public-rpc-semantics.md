# Public RPC semantics

本文是 SharpLink 公开 RPC 行为的 **code-review contract**。目标不是重复 API 参考，而是固定最容易被实现细节悄悄改变的边界。若实现、测试与本文出现冲突，应先判断公开语义是否真的要变化；语义变化必须在同一 PR 中更新测试和本文。

## Review quick reference

| Concern | Public semantic boundary |
| --- | --- |
| Deadline | 在逻辑调用创建时冻结为单调时钟 deadline；同一个 deadline 约束后续调用阶段和所有 retry，不会在重试、重选 endpoint 或 stream 启动后重新计时。此前的 `ConnectAsync`、transport dial、handshake、readiness wait 不属于这个 RPC deadline。 |
| `ConnectAsync` | 启动或加入 client 自己的连接生命周期；它不是“所有 endpoint 已 Ready”的屏障。需要 N 个 Ready endpoint 时使用 `WaitForReadinessAsync(N)`。 |
| `[OneWay]` | 成功只承诺本地发送边界成功，不承诺远端 handler 已执行，更不承诺业务成功。需要确认远端执行结果时使用 request/response RPC。 |
| Retry | 只可能发生在已准备的 `[Idempotent]` Unary。每次 retry 是新的物理 attempt，只能在该逻辑调用捕获的已准备连接中选择，并共享原 deadline。timeout/disconnect 不证明 Server 未执行过请求。 |
| Multi-cluster replace | 已取得的代理仍属于旧 child。要调用新 child，在切换后重新 `CreateProxy<T>()` 并 `PrepareAsync`，再通过 `Service` 调用。 |
| Server module / endpoint update | 已开始的调用不换代、不换连接。版本协调不迁移已准备的代理；重连或扩池后，要使用新连接须显式重新准备。 |

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
- deadline-bearing request 的本地 emission/flush 等待；
- Unary response wait；
- 有 deadline 的 stream lifetime / producer progress；
- retry decision、backoff/jitter 和后续所有 attempts。

两个容易误读的本地容量边界：

- 标准 generated Unary 当前使用有界 pending table 的**立即租用**。pending capacity 已满时会本地 `ResourceExhausted(PendingRequestCapacity)`，并不会默认排队等 slot。只有明确选择 wait-for-slot 的内部/扩展路径才存在 pending-slot wait；这种 wait 仍受同一个 deadline 约束。
- Request 进入 session send queue 也不是一个隐含的无限等待点。标准 request enqueue 在 send queue 满时会本地 `ResourceExhausted(SendQueueCapacity)`；deadline-bearing 调用对 emission/flush 的等待受原 deadline 约束，但已经入队的 Request 不会因此从物理队列撤回。

服务端收到带 TimeBudget 的请求后，admission queue 也受该调用 budget/cancellation 约束。Client 自己的绝对 deadline 同时继续运行，所以网络发送、传输和响应等待不会因为跨进程 TimeBudget 重新开始而延长 caller 的总预算。

### 1.3 `ConnectAsync`、dial、handshake、readiness 不属于 RPC deadline

RPC deadline 只在 RPC logical call 创建时出现。因此下面这些**此前的启动/连接阶段不会被后续 RPC timeout 追溯计费**：

- `ConnectAsync`；
- transport dial；
- protocol/TLS handshake；
- `WaitForReadinessAsync`。

它们有各自的生命周期与取消边界：`ConnectAsync` 的 caller token 只取消当前 caller 的等待，共享的 client-owned connection attempt 可以继续；`WaitForReadinessAsync` 的 token 取消 readiness wait；SharpLink RPC handshake 由独立的 `SharpLinkProtocolOptions.HandshakeTimeout` 约束，启用 TLS 的 transport 还可能有独立的 TLS handshake timeout。transport dial 服从 transport/client lifecycle 的取消机制，而不是某个尚未创建的 RPC deadline。

应用当然可以用同一个外部 `CancellationToken` 或更高层 orchestrator 同时限制“启动 + readiness + RPC”的总时间，但那是应用级 budget，不会把 SharpLink 的 RPC deadline 改造成 connect timeout。

### 1.4 TimeBudget 采样、入队与 emission

构造 Request 帧时从原 logical deadline 采样剩余 `TimeBudget`。帧准备完成后，pending publication gate 在入队前再次检查同一个绝对 deadline；没有 pending entry 的普通 OneWay 也在入队前检查。若 deadline 在此时已到期，Request 不得入队，也不会为这个未发布的 Request 发送 Cancel。

SendPump 按已接收的帧原样发送，不在 transport emission 重采样或改写 `TimeBudget`。因此，序列化、压缩和本地排队耗时不会再从已经写入帧的 budget 中扣除；Server 收到后按该 duration 建立自己的本地 deadline，Client 的原 deadline 则独立继续运行。

已经成功入队的 Request 可能在调用本地到期后仍到达对端。调用的 deadline 负责本地终态和取消，deadline-bearing 调用对 emission/flush 的等待也受原 deadline 约束；取消等待不等于撤回已入队字节，也不能证明 Server 未执行。

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
- **带 deadline 的 OneWay**：客户端会观察该 Request 所在 batch 的本地 emission；只有 `PipeWriter.FlushAsync` 成功后该发送阶段才成功，等待受原 deadline 约束。deadline 获胜时本地以 `DeadlineExceeded` 结束，但已入队的 Request 仍可能继续发出，随后发送的 Cancel 不保证阻止远端执行。

这两种成功都**不表示**：

- server 已收到 Request；
- server admission 已接受；
- handler 已开始或完成；
- 业务状态已经提交。

服务端 OneWay 在 admission overload 下还可能按配置直接丢弃；客户端因为没有 response 无法从返回值判断这一结果。

带 client stream 的 OneWay 还需要完成本地 stream producer 生命周期。无 deadline 时，初始 Request 被本地 SendPump 接收/入队后即可启动 producer；有 deadline 时，还先等待该 Request 的本地 emission/flush。pending cancellation/deadline 可以抑制尚未启动的 producer。这里没有逐请求的远端接受确认。

如果业务必须确认“远端 handler 已成功完成”或“副作用已经提交”，应使用有响应的 RPC，或在应用协议中设计明确 acknowledgement / idempotency mechanism，而不是把 `await OneWay` 当成远端确认。

## 4. 准备、换代与重连

先用 `CreateProxy<T>()` 获取本地代理，连接就绪后执行有时限的 `PrepareAsync`，再通过 `proxy.Service` 调用。默认准备一条物理连接上的契约路由；设置 `RpcProxyOptions.BalancePreparedEndpoints` 后，可在已准备的连接组内选路。每次逻辑调用在开始时捕获其路由或连接组；之后的准备只影响后续新调用。

在同一物理连接上准备多个契约时，逐个等待 `PrepareAsync`。连接一次只接受一项绑定控制操作；不同契约并行准备时，后到的操作可能收到 `ResourceExhausted`。这不限制已经准备好的契约并行发起业务调用。

每条物理连接最多占用 64 个准备绑定槽，正在排空的绑定也占槽。仅在客户端卸载契约不会立即归还远端绑定：协调换代可能仍需用原绑定识别来源。表满时，`PrepareAsync` 返回 `ResourceExhausted`，明细为 `SharpLinkErrorDetails.ResourceExhausted.PreparedBindingCapacity`；已有可用绑定不受影响。若服务端退役的绑定正在排空，等排空后再准备；若连接积累了仍未退役的绑定，在新的物理连接上重新准备所需契约。

### 4.1 Multi-cluster child replacement

Coordinator 的 `ReplaceClusterAsync` 先把 candidate child connect/validate 到可发布状态，再原子切换 slot，最后排空旧 child。

- 切换之后，重新 `CreateProxy<T>()` 以选中新 child，在新代理上 `PrepareAsync`，再调用 `Service`。
- 切换之前已取得的代理仍绑定旧 child。已开始的调用按旧 child 的排空和取消规则完成或失败；旧 child 停止后，旧代理的新调用会被拒绝，不转发到新 child。

### 4.2 Server-side dynamic service/module generation replacement

服务端发布新代不会把客户端已有的绑定改指向新代。

- `ReplaceAssemblyCoordinatedAsync` 替换整个程序集注册组。参与者须包含该组仍有打开绑定的所有支持协调的连接，即使它只绑定了同组另一个服务。已激活连接可在原连接准备同组其他代理；发布后未激活的跟随连接必须先换为新的物理连接，才能准备该组的新代。独立注册组不受影响。
- 已取得执行租约的调用保持原注册、服务和 Codec，直到物理退出；旧代完成排空后才可回收。
- 旧路由退役后，通过它发出的新调用不会被转到新代。本地契约注册未变时，可在满足上述连接条件后用同一代理执行 `PrepareAsync`；如果重建了客户端，需在新客户端创建代理。客户端 `Resumed` 才表示已恢复调用，服务端 `ActivationSent` 不等于这一步已完成。
- 在准入队列里等待只保留原 registration 的身份，不等于持有执行租约，也不会阻止旧模块卸载。排队时若原注册退役，有响应的调用以 `Unavailable` 拒绝；OneWay 不执行 handler，并按原请求形态排空。即使两代契约相同，也不混用旧调用的取消令牌和新代的服务或 Codec。

如果替换的是 **客户端可卸载契约程序集**，旧代理、类型和 Codec 仍属于旧 AssemblyLoadContext。用新一代的契约类型创建并准备新代理，并释放对旧对象的强引用，才能让旧 ALC 回收。双端操作见[在现有连接上协调服务换代](coordinated-version-change.md)；换代中断后的恢复步骤见 [3.0 迁移](rpc-channel-3.0-migration.md#换代中断后先查清单再重试)。

### 4.3 Dynamic endpoint topology / selection policy

连接 Ready 不等于该契约已准备。resolver 更新、扩池或重连得到的新连接须经显式 `PrepareAsync` 才能用于调用。版本协调只能在同一物理会话恢复已有代理的获准路由，不会迁移固定绑定或扩充连接组。其间仍可在已有、可用的已准备连接上调用。选择策略只在该调用的已准备候选内生效，已经选中的 attempt 不会中途换连接。

## 5. Retry 的公开边界

Retry 默认关闭。即使启用，也只有同时满足以下条件的调用才进入 retry pipeline：

1. RPC shape 是 Unary；
2. method 标注 `[Idempotent]`。

OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 都不 retry。自定义 `ISharpLinkRetryPolicy` 可以改变**符合资格的 Unary**对失败的决策，但不能把非幂等调用或 Streaming/OneWay 变成可 retry 调用。

默认 retryable error 是 `Unavailable` 和 `ConnectionClosed`。`MaxAttempts` 是**总 attempts 数，包含首次**。

每个 logical call 在开始时捕获 retry-policy generation 与已准备路由，并共享同一个 logical deadline。每次 retry：

- 可在捕获的已准备连接组内重新选路；单连接绑定不能通过 retry 扩成连接组，已失效的路由也不会由 retry 重新绑定；
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

ClientStreaming、Duplex 和带输入的 OneWay 在 Request 达到本地发送边界后就可以开始生产，不等逐请求确认。原待完成请求拥有启动决定；取消或 deadline 先终结请求时，不再启动生产者。服务端仍独立准入，准入前不向业务交付输入。

对 ServerStreaming / Duplex，输出端的初始 `Response` 不是整个 stream 的成功终态；正常终止由 `StreamComplete` 驱动。不要把输入获准或输出已开始理解为所有数据已处理完毕。

### Shutdown / drain 与 cancellation

Server 的 graceful `StopAsync(gracefulTimeout)` 会关闭新 call admission，并给已经开始的调用一个受 `gracefulTimeout` 限制的排空窗口；这个窗口不是新的 RPC deadline，已有 caller cancellation/deadline 仍继续生效。

Client `StopAsync` 是不同语义：它停止 reconnect、拒绝新调用、失败 pending work 并释放 client-owned resources；不要把它理解成“等待所有 in-flight RPC 自然完成”的 server-style graceful drain。无论哪一侧 shutdown，都不能把已经 terminal 的 call 恢复成成功。

## 7. Implementation and test evidence

重要语义都有现有自动化证据；修改这些边界时应同时检查对应测试：

| Semantic | Evidence |
| --- | --- |
| logical deadline 不重置、stream deadline | `test/SharpLink.UnitTests/Client/SharpLinkClientLogicalDeadlineTests.cs`, `ClientStreamProducerDeadlineTests.cs` |
| TimeBudget 在构帧时采样、入队前拒绝已到期 Request、入队后 deadline 结束本地等待而不撤回帧 | `SharpLinkClientTimeBudgetTests.cs`, `SharpLinkClientDeadlinePublicationTests.cs`, `SharpLinkClientTimedUnaryEmissionTests.cs`, `SharpLinkClientOneWayTimeBudgetTests.cs` |
| timeout/cancellation | `SharpLinkClientCallOptionsTests.cs`, `SharpLinkClientTimeoutTests.cs`, `SharpLinkClientCancellationTests.cs` |
| connect / readiness snapshot / wait | `SharpLinkClientLifecycleStartStopTests.cs`, `SharpLinkClientReadinessStateTests.cs`, `SharpLinkClientReadinessWaitTests.cs`, `SharpLinkClientReadinessPublicationTests.cs` |
| retry eligibility、endpoint reselection、deadline | `SharpLinkClientRetryBehaviorTests.cs`, `SharpLinkClientRetryDeadlineTests.cs`, `SharpLinkClientRuntimeRetryPolicyTests.cs` |
| multi-cluster replace / rollback / proxy binding | `SharpLinkMultiClusterMutationTests.cs`, `SharpLinkMultiClusterMutationConcurrencyTests.cs`, `RuntimeMultiClusterIntegrationTests.cs` |
| dynamic module generation replacement / drain / unload | `test/SharpLink.IntegrationTests/RuntimeAssemblyIntegrationTests.cs` |
| runtime endpoint policy 与 attempt capture | `EndpointSelectionRuntimeInteractionTests.cs`, `EndpointSelectionRuntimeTests.cs` |
| 已准备路由、双端协调与输入启动边界 | `RuntimeAssemblyIntegrationTests.NStudyTwoWaySite.cs`, `PreAdmissionStreamActivationRaceIntegrationTests.cs` |

可运行示例：`demo/Timeout`, `demo/Cancel`, `demo/Oneway`, `demo/Resilience`, `demo/MultiCluster`。

更完整的主题文档见 [调用、流式与取消](calls-and-streaming.md)、[服务发现与韧性](resilience.md)、[Server admission](admission-control.md) 和 [多集群与动态模块](dynamic-modules-and-multicluster.md)。
