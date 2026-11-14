# 调用、流式与取消

## 创建与准备契约

`client.CreateProxy<TContract>()` 同步创建本地 handle，不等待连接、远端 Manifest 或 Bind。
在启动业务调用前，对这个 handle 显式执行一次有限预算的
`await proxy.PrepareAsync(TimeSpan.FromSeconds(10), cancellationToken)`，再保留并使用
`proxy.Service`。没有已绑定合法目标时，默认调用以 `Unavailable/NoBoundRoute` 失败，
不会序列化业务参数或启动输入 producer。连接 Ready 不代表契约已绑定；重连或替换后必须
在新物理 Session 上重新绑定，不能搬用旧 binding token。

## 用注册结果准备精确服务

控制侧需要把一次动态注册的确切结果交给数据侧时，使用 Server 的
`RegisterAssemblyWithReceipts` 或 `ReplaceAssemblyWithReceiptsAsync`。返回值分别包含原有
`Registration` / `Replacement` 结果，以及同一次发布捕获的只读 `Receipts`；按 ContractId
选择对应服务。只有契约、没有 Service 的发布和失败结果均返回空集合。

```csharp
var publication = server.RegisterAssemblyWithReceipts(serviceAssembly);
if (!publication.Registration.Succeeded)
    throw new InvalidOperationException(publication.Registration.Error!.Message);
var receipt = publication.Receipts.Single(item => item.ContractId == contractId);

var rpc = client.CreateProxy<IMyService>(new RpcProxyOptions
{
    Receipt = receipt,
    Requirement = RpcRegistrationRequirement.ExactRegistration
});
await rpc.PrepareAsync(TimeSpan.FromSeconds(10), cancellationToken);
await rpc.Service.GetAsync(id, cancellationToken);
```

receipt 使用 Contract 所有者的 hash 和 Service 的注册 ID/发布代次；分离程序集时不能把 Service
程序集 hash 当成契约 hash。替换等待旧工作退出期间即使又发布了下一代，结果仍返回本次新代的
receipt，不重新查 latest。原来的 `RegisterAssembly` / `ReplaceAssemblyAsync` 及共享结果类型
不变。`GetRegistrationReceipt` 只读取精确程序集当前拥有的注册，不能代替某次操作的原始结果。

`Compatible` 检查契约/hash，显式 receipt 还限定同一 Server/epoch；`MinimumVersion` 加上该服务
自身的最低发布代次；`ExactRegistration` 再限定原注册 ID。receipt 不预留容量或授权；旧代退休后，
旧 Exact receipt 不能降级到新代。替换等待取消不会回滚已发布的新代，也不返回结果；物理清理仍由
Server 完成。`Replacement.ReferencesReleased` 说明旧代回收状态，不是新代可调用或 Bind 完成的证明。

## 超时、RpcDeadline 与 TimeBudget

Client 不再隐式选择请求超时 fallback。每个 `SharpClientBuilder` 必须在 Build 前显式三选一：`UseRequestTimeout()` 使用推荐的 30 秒 Unary fallback，`UseRequestTimeout(timeout)` 使用自定义 fallback，`DisableRequestTimeout()` 明确关闭 Client-wide fallback；保持未指定状态会使 Build 失败。这个 Client-wide fallback 只自动应用于普通 Unary 调用；OneWay、ClientStreaming、ServerStreaming 和 Duplex 不自动继承它。流式/OneWay 调用若要携带本地 `TimeBudget`，应使用方法 `[Timeout]`，或继承已有父调用 lifetime。方法 `[Timeout]` 是方法级策略，会覆盖 Client fallback；例如 Client custom fallback 为 30 秒、方法 `[Timeout(120)]` 时，该方法的本地策略为 120 秒，而不是两者取最小值。无参数 `[Timeout]` 继续表示使用 Client fallback，因此 Client 选择 `DisableRequestTimeout()` 时不能凭空产生一个 fallback。

Runtime 将选中的 `Timeout` 解析为进程本地、基于 monotonic clock 的 `RpcDeadline`。请求真正发出前再计算剩余 `TimeBudget` 并写入 wire；Server 收到后用自己的 monotonic clock 解析新的本地 `RpcDeadline`。因此 Client/Server 不依赖墙钟同步，wire 也不再传播绝对 UTC deadline。

当服务处理一个已有上游 `TimeBudget` 的 RPC 并继续发起下游 RPC 时，上游剩余 lifetime 是真正的上限：先选择下游方法/Client 的本地 timeout policy，再用父调用的剩余 `TimeBudget` 做 cap。中间 hop 不会重启原始 timeout。到期错误为 `DeadlineExceeded`，调用方显式取消为 `Cancelled`。Client Activity 使用低基数 `rpc.sharplink.lifetime_source` 标识实际 lifetime 来源：`method_timeout`、`client_recommended_timeout`、`client_custom_timeout` 或 `inherited_time_budget`。`demo/Timeout` 和 `demo/Cancel` 展示两种终止路径。

## Metadata

Metadata 是 RPC envelope state，不是业务合同参数。调用方通过 `CreateProxy<TContract>` 的
`RpcProxyOptions.Metadata` 为这个 proxy 固定 metadata；需要不同值时创建另一个 handle，
不要修改 ambient/global state。例如：

```csharp
var tenantProxy = client.CreateProxy<IMyService>(new RpcProxyOptions
{
    Metadata = new SharpLinkMetadata(
        new System.Collections.Generic.KeyValuePair<string, string>("tenant", "tenant-a"))
});
await tenantProxy.PrepareAsync(TimeSpan.FromSeconds(10), cancellationToken);
await tenantProxy.Service.GetAsync(id, cancellationToken);
```

Client interceptor 仍可通过 `SharpLinkClientInvocationContext.Metadata` 为横切策略补充/变换当前逻辑调用的 metadata；不要用调用顺序或全局可变 interceptor 状态模拟 caller-selected metadata。服务端从 `SharpLinkCallContext.Current?.Metadata` 或 Server interceptor context 读取。Metadata 受 `MaxMetadataBytes` 限制，适合低基数路由/诊断信息，不适合大对象、凭据日志或无限增长标签。

## Streaming

流式 item 逐项编码，受 stream 和 connection 两级字节窗口约束。消费者只有实际取走 item 后才归还 credit；慢消费者因此限制发送方，而不是无限缓冲。每个接收 dispatcher 最多缓存 4096 个元素，此外仍受字节窗口和 frame 上限约束。

提前停止消费时必须释放异步枚举器。Client 会发送 `ConsumerAbandoned` Cancel，关闭本地 stream、归还已消费 credit，并抑制迟到响应。不要把返回的 `IAsyncEnumerable<T>` 交给多个并发消费者；框架只支持单消费者。

## ClientStreaming 与 Duplex

ClientStreaming 和 Duplex 在收到本次调用的 `RequestAccepted` 后才启动客户端流泵，
Bind 成功本身不能替代调用准入。ServerStreaming 和 Duplex 在调用时捕获 policy/deadline，
在首次枚举时取得本地动态模块所有权。请求与每个 stream 使用同一连接；重连或 retry 不会跨连接迁移已开始的流。Streaming 和 OneWay 不自动 retry，只有标注 `[Idempotent]` 的 Unary 才进入 retry policy。

## OneWay

OneWay 成功表示请求已进入本地发送/服务端接收流程，不包含业务结果。服务端接入控制默认不排队 OneWay；超限时丢弃并记录指标。只有明确设置 `QueueOneWayCalls` 才允许等待。业务失败只能通过服务端日志、Activity、指标或应用补偿观察。

OneWay 输入不使用逐请求 Ack：专用输入请求携带 1–127 个流的独立有界 count，
服务端可在 binding/method 被拒绝时安全丢弃并回收 credit/route；矛盾形态或无法有界清理时关闭 Session。

## `[NonCancellable]`

没有 `CancellationToken` 的 RPC 必须显式标注 `[NonCancellable]`。调用方取消后不再等待并清理框架资源，但服务实现可能继续运行；适用于确实不可取消、可接受后台完成的短任务。长任务应接收 token 并及时观察。

完整调用形态见 `demo/Streaming`、`demo/Oneway`、`demo/Cancel` 和 `demo/Timeout`。

## 3.0 扩展边界

业务通过生成的契约 proxy 调用 Unary、OneWay 和三类 Streaming；手写调用通过
client-issued `RpcLocalContract` 创建同一模型的 `IRpcContractChannel`。Generated API 6
用这个契约 channel/client-stream sink 与 `IRpcGeneratedServerBridge` 连接调用生命周期；
bridge 不公开 Session、dispatcher 或 flow-control mutator。所有框架后台发送/排空仍由 owner
监督并在停止时等待；应用提前停止枚举必须释放 enumerator。调用时间只传播剩余 TimeBudget
（Protocol v2 minor 5，最低兼容 minor 5），不读取跨机器绝对 deadline。
旧 `Get`、`GetWithMetadata` 和 client-wide `IRpcChannel` 已移除，没有兼容转发。
3.0 Codec/API 变化见 [3.0 migration](unsafe-blit-3.0-migration.md)；历史 1.1.1→2.0 迁移见 [migration](migration.md)。
