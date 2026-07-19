# 更新日志

本文档记录项目的重要变更，格式参考 Keep a Changelog，并遵循语义化版本。

## [Unreleased]

## [0.7.0] - 2026-07-20

### 新增

- 增加显式启用、仅限同机同一用户的实验性共享内存传输，以及 Client/Server Builder 的 `UseSharedMemory` 配置入口。
- 每条连接使用双向 SPSC 共享内存环传输 RPC 数据；命名管道控制通道只负责有界握手、合并唤醒、关闭和进程存活检测。
- 增加容量、SpinCount 与握手超时配置，并按 LowLatency、Balanced、Throughput profile 提供默认值；双方容量不一致时协商较小值，初始化失败不会静默降级。
- LoadTest、StreamLoadTest、Chaos、PackageSmoke 和 NativeAOT smoke 增加 SharedMemory 模式与结构化性能证据。

### 变更

- 共享内存读写管线支持环内直接写入/读取、分段回卷、有界池化 spill、背压和可复用异步等待，详细热路径计数仅在显式诊断模式启用。
- 映射采用版本化布局和当前用户私有目录；握手校验 nonce、路径、权限与布局，RPC 认证、授权、deadline、流控和心跳继续生效。

### 修复

- 修复共享等待标志可能丢失唤醒、竞态游标快照被误判为数据损坏，以及映射校验失败、握手失败和关闭路径中的资源清理问题。
- 修复通知合并、满环 spill、取消后恢复 Flush、连接强杀与 listener 重启等竞态；连接释放不会等待对端腾出环空间。

### 性能与稳定性

- macOS arm64 完成 Release JIT、独立进程 NativeAOT、包消费与两轮 10 分钟 SharedMemory Chaos；最新一轮为 4,308,099 次成功、0 次非预期失败，结束后指标、活动调用、临时映射与测试进程归零。
- 32 B / LowLatency 的单轮方向性样本中，SharedMemory 在 c1/c8/c32/c128 吞吐均领先 UDS；正式五轮性能矩阵、Windows/Linux 运行时与 NativeAOT、2 小时及 24 小时门禁仍待完成，因此该传输仍为实验性功能，不进入正式支持矩阵。

完整设计、正确性证据、性能数据和未完成门禁见 `doc/shared-memory-experiment.md`。

## [0.6.10] - 2026-07-18

### 新增

- Protocol minor 2 增加可协商的 `CancellationReason` capability；协商后 Cancel 帧携带 `UserCancellation`、`DeadlineExceeded` 或 `ConsumerAbandoned`，与 0.6.9 对端仍使用空载荷 Cancel 互操作。
- Source Generator 增加 `SHARPLINK014`：Streaming 契约缺少 `CancellationToken` 时编译失败；`[NonCancellable]` 可显式豁免。增加 `SHARPLINK015`，拒绝特性与 Token 同时声明。
- `sharplink.calls.abandoned` 增加低基数终止原因标签；新增 `sharplink.responses.late_dropped` 指标。

### 变更

- 生成 Stub 将显式 Token、任意 client stream 参数和 stream 返回值都声明为框架可取消；即使业务方法标记 `[NonCancellable]`，stream pump、dispatcher、窗口等待和连接资源仍可终止。
- 服务端在请求入口把绝对 UTC deadline 换算为 monotonic timestamp，之后的到期调度和响应仲裁不再受 wall clock 调整影响。
- 服务端 deadline 使用每物理连接一个 Timer 扫描最多 1,024 个在途调用；正常完成路径不维护 timer node，也不进入 scheduler lock。
- 每连接迟到响应 Warning 最多五秒一次，并携带前一窗口被抑制的数量；迟到响应 metric 仍逐次记录。

### 修复

- 修复 deadline CTS 先取消业务 Token、后发布 `DeadlineExceeded`，导致业务取消回调可能观察到空或错误终止原因的竞态。
- 非协作调用不再仅因携带 deadline 创建 invocation CTS；超时后继续观察用户 Task，但抑制其迟到成功或异常响应。
- deadline 扫描快照同时保存 request ID 与池化 state；旧代扫描不能获取已经归池并被新请求租用的对象。
- 修复无业务 Token 的 server streaming 提前停止消费后，框架流泵可能无法及时终止的问题。
- 修复本地 stream 取消与已取得的异步 dispatch 竞争时，Cancel 可能先于最后的 WindowUpdate 到达并使对端把合法 credit 误判为协议错误、关闭健康连接的问题。
- 修复连接池扩容恰逢滚动重启失败后可能没有把零 Ready 池交给持久重连 worker，客户端永久停留在 `Draining/Reconnecting` 的问题。
- Ready connection snapshot 现在是请求选择的事实来源；全局状态发布的瞬时滞后不再拒绝已就绪连接。GoAway 排空且暂无连接返回 `Unavailable`，只有 Client Stop/Dispose 返回 `ConnectionClosed`。
- 修复 StreamManager 终止 drain 与迟到 stream 注册竞争时，dispatcher 可能挂在已经移出 map 的节点上并永久多计一个 active stream 的问题；正常注册使用两次终态读取，不增加全局锁。
- Server call admission 现在区分排空中的 `Unavailable` 与真实容量耗尽的 `ResourceExhausted`，不再把 Request accept 后发生的停机竞态错误归类为限流。

### 性能与稳定性

- `ServerCallCancellationState` 专项基准中，cooperative deadline 从实验性独立 CTS 的 368 B/op 降至 80 B/op；相对 0.6.9 的 320 B/op 也显著下降。non-cooperative deadline 从 320 B/op 降至 32 B/op。
- 增加 100,000 次客户端 Response/Cancel/Deadline 和服务端 Cancel/Response/Deadline 终态竞态、10,000 次真实 stream early-break、10,000 次 stream register/drain 竞态，以及 Stop/Connection 并发取消测试。
- 五轮交替 A/B 的所有 Unary/Streaming 场景均通过 97%/105% 门禁；`Rpc_Add` ShortRun 保持 672 B/op。
- Stream register/drain 修复的第一版全局锁实验因 QPS -7.54%、P99 +14.34% 被撤销；最终无锁版本专项五轮 A/B 为 QPS +5.31%、P99 -8.27%，两边均为零失败。
- 最终代码提交的 2 核 120 秒混合 Chaos 完成 2,632,568 次成功调用、11 次滚动重启与 0 次非预期失败；最大恢复 331ms，最终所有框架 gauge 为 0。

完整协议、迁移和证据见 `doc/protocol-v2.md`、`doc/migration-0.6.10.md`、`doc/performance-0.6.10.md` 与 `doc/chaos-0.6.10.md`。

## [0.6.9] - 2026-07-17

### 新增

- 增加 `SharpLink.ChaosTests`，覆盖混合 Unary/Streaming、提前停止消费、取消/deadline、滚动 TCP 重启、重连和最终框架指标归零。
- 增加 PR 两分钟、Nightly 两小时 Chaos 分级 Gate，以及专用宿主连续 24 小时长稳脚本与结构化 JSON 证据。
- 增加 JIT/NativeAOT 性能矩阵入口，覆盖 Transport、Profile、连接池、payload、并发、Unary、OneWay、同步/异步服务和 Streaming。
- LoadTest 增加 Empty Unary、OneWay 与 AOT-safe source-generated JSON 报告。

### 变更

- `StopAsync` 使用 graceful timeout 与固定五秒框架清理预算有界返回；不合作的业务 Task 不再永久阻塞宿主停机。
- 仍在执行的用户调用保留自身 DI scope/provider，并在真实结束后延迟清理；listener、session、Pipe、send queue 等框架资源不被业务 Task 长期保留。
- Stream Dispatcher 静态池改为每个 item 类型最多保留 1,024 个对象；大于 256 的缓冲在回池前缩回初始容量并清除引用。
- OneWay 性能证据区分正常单生产者吞吐与主动耗尽有界发送队列的 backpressure 场景。

### 修复

- 修复 server/duplex stream 提前停止消费时，WindowUpdate、Cancel、pending slot、dispatcher 租约和 send credit 之间的竞态与泄漏。
- 修复 dispatcher 在旧调用仍持有 dispatch entry 时过早回池，随后被另一调用复用并被迟到完成污染的问题。
- 修复 server/duplex stream 在异步等待连接注册期间被消费者释放后提前回池，恢复线程随后把已清空 Codec 的 dispatcher 注册到新连接的问题。
- 修复 Cancel 到达已完成调用后，响应 stream send state 未被终止并可能重新创建 credit 状态的问题。
- 修复 Session 已终止后迟到的 `NotifyConnected` 重新增加 active-connection 指标、且再无关闭机会抵消的问题。
- 修复 TLS 重连测试可能把上一连接代际尚未清空时的旧 Ready 状态误判为新连接已经可用的问题。
- 修复握手帧与后续 GoAway/首个 Request 共用同一 Pipe buffer 时，未消费尾帧被错误标记为已检查并一直等待新字节的问题。
- 重连改为实例级持久 supervisor：容量 1 的故障信号不会在 worker 退出边界丢失；同时已被立即排空的连接不再发布全局 Ready，连接归零后会持续补充替代连接直到 Client Stop。
- 修复 PendingCall 发布到 slot 后、owner active count 注册前被 Cancel/断连取走，造成 active count underflow 并击穿客户端读循环的竞态。
- 修复 NativeAOT LoadTest 报告依赖反射 JSON 序列化的问题。

### 性能与稳定性

- 五轮 TCP Unary A/B：c1 QPS +1.86%、P99 持平；c128 QPS -0.15%、P99 -1.82%，全部零错误并通过门禁。
- 五轮 Server Streaming A/B：QPS -1.45%、P99 -2.49%，零错误并通过门禁。
- `Rpc_Add` 保持 672 B/op；JIT/NativeAOT smoke 正常矩阵全部零错误，AOT publish 零 trimming/AOT 警告。
- 两分钟混合 Chaos 完成 2,943,483 次成功调用和 9 次滚动重启；以连续五次探针确认稳定恢复，最大端到端恢复 15.583 秒，非预期失败为 0，结束时所有框架 gauge 为 0。

0.6.8 → 0.6.9 没有公共 API 或 Protocol v2 wire 变更。完整证据见 `doc/performance-0.6.9.md`、`doc/chaos-0.6.9.md` 和 `doc/migration-0.6.9.md`。

## [0.6.8] - 2026-07-17

### 新增

- `ServerConnectionState` 统一拥有物理连接的 Session、认证上下文、最后接受的 request ID、每连接调用额度、取消表、连接 token 与 Handshaking/Ready/Draining/Closed 生命周期。
- `ServerCallCancellationState` 为远端 Cancel、deadline、Server Stop、连接故障和正常完成提供 first-wins 终态仲裁与独立错误分类。
- 增加连接认证隔离、幂等关闭、独立 admission、deadline timer、抛异常取消回调及 10,000 次取消/完成/Dispose 竞态测试。

### 变更

- 服务端三个 session 字典合并为单一 connection dictionary；业务请求热路径直接持有 connection state，不再逐请求查认证上下文或最后请求 ID。
- 心跳超时、同 ID 连接替换、读循环退出和 Server Stop 统一经过幂等连接关闭入口；单连接故障只取消该连接的服务调用。
- 框架任务继续被显式持有、等待和观察异常；异步用户调用改由 active-call counter 与统一 observer 收敛，不再为每个调用进入全局 Task HashSet 锁。

### 性能

- 同机 TCP c128 对同步、`Task.Yield()` 和 1 ms async 服务各执行五轮交替 A/B，全部零错误并通过 QPS/P99 回归门禁。
- 目标 `Task.Yield()` 路径 QPS +0.08%、P99 -1.54%，未达到可宣称性能收益阈值；本版本只确认全局任务集合锁从该路径消失，不宣称平均吞吐提升。
- StreamFlowController、Writer Pool、Interceptor pipeline、Throughput flush timer 与 Generated Stub Codec lookup 均因缺少触发阈值证据而保持不变。

### 修复

- 修复连接替换或心跳关闭只释放 Session、但不完成统一连接生命周期和连接级服务调用取消的问题。
- 修复携带 deadline 的调用可能把远端取消、服务停机或连接故障误归类为 `DeadlineExceeded` 的竞态。
- 修复已完成 framework task 在 Stop 获取快照前移出集合时，异常可能未被观察的问题。
- 修复 Windows Release Gate 的 TLS PFX 导出、NamedPipe 测试 flush 顺序和 PackageSmoke NuGet 源隔离问题。

完整 A/B、附加实验和保留结论见 `doc/performance-0.6.8.md`。

## [0.6.7] - 2026-07-17

### 新增

- 每条物理连接独立拥有 `ClientConnection`、pending table、request ID、monotonic deadline timer、取消源、active count 与 Ready/Draining/Closed 状态。
- `[NonCancellable]` 显式声明服务方法不支持协作取消；Source Generator 通过 `SHARPLINK004` 警告未声明 `CancellationToken` 的 RPC 方法。
- `sharplink.calls.abandoned` 指标与限频结构化日志，记录客户端已放弃但服务业务仍在执行的调用。

### 变更

- Response、error、用户取消、deadline、断连、GoAway、send failure、stream complete 与提前停止消费统一经过每连接 `PendingCallTable.TryComplete` 仲裁。
- 每连接只使用一个 monotonic timer 扫描有界 pending table；正常完成不再进入旧 timeout scheduler 的 Schedule/Cancel 锁。
- 默认 Unary timeout 仍为 30 秒并固定映射为 `SharpLinkException(DeadlineExceeded)`；`DisableRequestTimeout()` 提供真正无客户端默认 timeout 的显式入口。
- 客户端 deadline 与服务执行取消解耦：没有 `CancellationToken` 的服务调用会在客户端超时后标记 Abandoned、抑制迟到响应，并在业务任务真实结束后释放 admission 与 DI scope。
- Server/duplex stream 提前 Dispose 会发送 Cancel，并一次性释放 pending slot、dispatcher、producer、credit waiter 与 active count。

### 性能

- 同步且 `[NonCancellable]` 的服务调用不租用 cancellation state、不进入服务端 cancellation map；只有支持协作取消或真正异步未完成的调用才注册状态。
- 同机五轮 A/B 中位数相对 `v0.6.6`：c1 QPS +0.59%，c128 QPS +0.33%，c128 P99 -16.82%，`Rpc_Add` allocation 保持 672 B/op。
- 已撤销“所有服务调用无条件注册 cancellation state”的实验；其拆分测量导致 c128 相对客户端候选回退约 10%。

### 修复

- 迟到 response 不再需要永久 tombstone，也不会完成已复用的 request ID 或关闭健康连接。
- 修复提前退出 server/duplex stream 后客户端 pending、dispatcher 与 active count 泄漏。
- 修复只因请求携带 deadline 就把任意 `OperationCanceledException` 误报为 `DeadlineExceeded` 的错误分类。
- Cancel、deadline、response 与 disconnect 竞态只允许一个终态，operation 仍在调用方 `GetResult` 后才回池。

完整 A/B 环境、失败实验和最终结论见 `doc/performance-0.6.7.md`。

## [0.6.6] - 2026-07-17

### 新增

- LoadTest、StreamLoadTest 与热路径 Micro Benchmark 增加可重复 JSON 证据，记录 commit、机器、Runtime、GC、Transport、Profile、连接池、payload 与并发度。
- `IRpcBufferWriterPool.Rent(int maxWrittenBytes)` 与 session 协商后的出站帧硬上限。
- Server Interceptor 异步等待期间使用有界 ArrayPool owner 持有业务 arguments，避免越过 `PipeReader.AdvanceTo` 后读取复用内存。

### 性能

- SendPump 没有 admission waiter 时跳过容量通知锁；默认单连接池达到上限时跳过扩容状态锁。
- Generated Stub 的多段固定宽度参数改用有界 stack scratch，不再为每个参数分配临时数组。
- `SharpLinkCallContext` scope 改为值类型；基准从约 104 B/op 降至 72 B/op。
- Parser 只做帧结构验证，Metadata/Error/Handshake 等语义 payload 在消费位置解析一次；Metadata parser 基准降为 0 B/op。

### 变更

- 删除进程级 `BufferWriterPool`、`RuntimeConcurrency`、`RpcCodecRegistry`、`RpcCodec` 兼容入口；Codec、Pool 与状态容器配置只属于构建它们的 Client/Server Context。
- 删除旧 Client 调用排列组合和 CallOptions wrapper；生成代理只使用 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类 `IRpcChannel` invoker。
- 测试与示例迁移到实例级 Runtime Context、`SharpLinkBufferWriterPool` 和生成调用链。

### 修复

- Client/Server 所有业务帧、流帧与错误帧统一执行双方握手协商后的较小 frame limit；本地超限返回 `ResourceExhausted` 且不关闭健康连接。
- 修复启用异步 Server Interceptor 后请求参数可能引用已归还 Pipe buffer 的生命周期问题。

完整 A/B 环境、数据和结论见 `doc/performance-0.6.6.md`。

## [0.6.0] - 2026-07-17

### 新增

- TCP TLS 与双向证书认证，TLS 在 Protocol v2 handshake 前完成并使用独立超时。
- TLS 协议/cipher 结构化日志；默认保留平台证书链和 hostname 校验。
- `ISharpLinkClientAuthenticator`、`ISharpLinkServerAuthenticator`、二进制认证请求与 delegate adapter。
- 显式 `RequireAuthentication()` Build 校验；默认模式保持 Anonymous。
- Client/Server interceptor pipeline、实例级 `IRpcExceptionMapper` 与显式 `[Idempotent]` 方法元数据。
- `SharpLink.Client` / `SharpLink.Server` ActivitySource 与 `SharpLink` Meter；覆盖连接、调用、字节、队列、pending、stream 和失败指标。
- instance/type/factory 三类服务注册、Singleton/Scoped/Transient 生命周期与宿主 DI scope。
- Protocol v2 health-check capability、`CheckHealthAsync`、本地 readiness 和 Microsoft health checks。

### 变更

- 删除 string/bool authenticator Builder API；认证 payload 在每次重连时异步创建并受 handshake 上限约束。
- 认证上下文挂入每次服务调用；handshake 自动拒绝已过期 context，授权 helper 可在调用前再次校验 expiry/scope/tenant。
- 未注册 interceptor 时生成调用继续直达泛型 invoker；注册后客户端可修改调用选项或短路，服务端可鉴权、限流与审计。
- 未映射业务异常默认只公开 `Internal` 和通用消息；详细错误需要显式启用，stream 错误使用同一 mapper。
- Activity/Meter 无 listener 时不构建 tag collection、Activity 或调用 observer；结构化日志继续使用 `LoggerMessage` 预编译路径。
- 默认服务生命周期保持 Singleton 热路径；Scoped/Transient scope 覆盖完整调用或 stream，并在异常、取消、断线和停机时释放。
- Server 停机顺序固定为 readiness=false、停止 accept、GoAway、等待在途调用、超时取消、flush 与资源释放。

### 修复

- Source Generator 辅助 request/stream 类型加入契约名前缀，多个接口出现相同 method hash 时不再发生编译期类型冲突。
- NativeAOT 服务注册显式保留 DI 所需 public constructor，避免服务构造器被 trimming 后在首个调用失败。

## [0.5.0] - 2026-07-17

### 新增

- 可异步 admission 的 `PendingRequestTable`，完整 64 位 request ID 匹配与统一完成仲裁。
- `PooledByteBufferWriter`、明确的 frame owner 生命周期与 Context 有界 writer pool。
- Source Generator 原生 DTO/闭合集合 Codec、稳定字段 ID、未知字段跳过、required 校验与 64 层类型图边界。
- `[RpcSerializable]`、`[RpcMember]`、`[RpcIgnore]`、`[RpcRequired]`、`[RpcExternalCodec]`。
- append-only generated Codec manifest；Runtime Context 在 Build 时冻结 manifest 快照。
- Protocol v2 stream/connection 双层字节窗口、`WindowUpdate` 与单个大帧临时借用。
- 每 Endpoint 有界客户端连接池、压力扩容与 power-of-two choices 连接选择。

### 变更

- 生成代理收敛到 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类内部 Invoker；静态 descriptor/Codec 避免捕获 delegate。
- `IRpcCodec<T>.Serialize` 统一写入 `IBufferWriter<byte>`；协议回填使用 `IRpcByteBufferWriter`。
- AOT Smoke 不再依赖 MemoryPack，覆盖 class、record、struct、嵌套数组和生成 manifest。
- client/server stream sender 在额度不足时异步等待；消费、取消、超时和断连统一释放额度等待者。
- stream dispatcher 按已编码字节记账，迟到的已取消 stream data 只丢弃并计数，不重建 dispatcher。
- stream 调用固定绑定创建时的连接；`GoAway` 连接停止接收新请求并在变为空闲后退出，其他健康连接继续服务。
- LoadTest 与 StreamLoadTest 支持 `--min-connections` / `--max-connections`。

### 修复

- 长请求跨越 pending table ID 周期、乱序响应、cancel/timeout/disconnect 竞态不会误命中新请求或重复归还 operation。
- client/server stream 的额度等待、取消、迟到数据与 terminal ACK 统一收敛，慢消费者不再驱动无界缓冲。
- 多连接下单条 session 断开只失败绑定到该连接的请求，不影响其他 ready session。

## [0.4.0] - 2026-07-17

本版本合并原实施路线图的 0.4 安全基线与 0.5 Runtime/Protocol v2 重构。

### 新增

- 实例级 `SharpLinkRuntimeContext`、性能预设与不可变配置快照。
- TCP/UDS、NamedPipe、AnonymousPipe 的 Client Factory、Server Listener 和独立 Connection 模型。
- 15 字节固定头的 Protocol v2、能力协商、Ping/Pong、Cancel、GoAway、deadline 与 metadata。
- `SharpLinkCallOptions`、`SharpLinkMetadata`、扩展错误码及服务端调用上下文。
- 字节有界单写者 SendPump、强制 flush marker 与资源耗尽保护。
- Client/Server 原子生命周期、自动重连、断连单次收敛和优雅排空。
- NuGet-only PackageSmoke；`SharpLink.Sdk` 自动携带 Source Generator。

### 变更

- `ConnectAsync` 成功返回、失败抛结构化异常，不再返回 `bool`。
- Client/Server 主生命周期统一为 `StopAsync` / `DisposeAsync`。
- 默认 Unary timeout 为 30 秒；stream 默认不设置 timeout。
- 自定义 Codec、Buffer Pool 与并发配置归属实例 Context，不再互相覆盖全局状态。
- 默认认证明确为 Anonymous；删除默认 Password 和旧字符串错误 wire 格式。
- 所有正式包目标框架更新为 .NET 10。

### 修复

- Parser/Codec 在切片、分配前验证所有网络长度，非法数据稳定映射为协议/数据错误。
- pending request、stream、server concurrency、anonymous pipe offer 与 writer retention 增加硬边界。
- 修复 timeout 调度器取消墓碑导致的吞吐退化，取消节点现在立即从有界堆移除。
- 修复 send/dispose、heartbeat/stop、read/write fault 和 GoAway/new request 等生命周期竞态。
- CI 与 PackageSmoke 使用隔离 NuGet 缓存，避免同版本旧包污染验证。

### 兼容性说明

- 本版本包含公共 API 与线协议破坏性升级，不提供 Protocol v1 兼容层。
- c32 Unary QPS 为前一基线的 96.75%，保留原基线并在 0.5.1 优先处理。

## [0.2.0] - 2026-02-18

### 新增

- 引入 `RpcCodecRegistry`，支持统一注册与复用类型 Codec。
- 引入可选 MemoryPack 回退序列化器。
- 增强 blittable struct 容器编解码。

### 变更与修复

- 区分正常关闭与异常断连日志。
- 修复 Hosting 正常停机噪音与 pending request 失败传播问题。
- 本版本包含破坏性变更。

## [0.1.0] - 2026-02-12

### 新增

- 初始化 Abstractions、Runtime、Client、Server、SDK、Hosting 与 Generator。
- 支持 Unary、OneWay、客户端流、服务端流和双向流。
- 增加示例、Unit/Integration/AOT/Load/Benchmark 测试和 CI 工作流。

### 变更与修复

- 优化流式调用固定分配。
- 修复初始生命周期、取消和断连边界问题。
