# 调用、流式与取消

## 超时、RpcDeadline 与 TimeBudget

Client 默认请求超时 fallback 为 30 秒，可用 `UseRequestTimeout` 修改默认值，或用 `DisableRequestTimeout` 关闭默认值。这个 Client-wide fallback 只自动应用于普通 Unary 调用；OneWay、ClientStreaming、ServerStreaming 和 Duplex 不自动继承它。流式/OneWay 调用若要携带本地 `TimeBudget`，应使用方法 `[Timeout]`，或继承已有父调用 lifetime。方法 `[Timeout]` 是方法级策略，会覆盖 Client 默认 fallback；例如 Client 默认 30 秒、方法 `[Timeout(120)]` 时，该方法的本地策略为 120 秒，而不是两者取最小值。无参数 `[Timeout]` 继续表示使用 Client 默认策略。

Runtime 将选中的 `Timeout` 解析为进程本地、基于 monotonic clock 的 `RpcDeadline`。请求真正发出前再计算剩余 `TimeBudget` 并写入 wire；Server 收到后用自己的 monotonic clock 解析新的本地 `RpcDeadline`。因此 Client/Server 不依赖墙钟同步，wire 也不再传播绝对 UTC deadline。

当服务处理一个已有上游 `TimeBudget` 的 RPC 并继续发起下游 RPC 时，上游剩余 lifetime 是真正的上限：先选择下游方法/Client 的本地 timeout policy，再用父调用的剩余 `TimeBudget` 做 cap。中间 hop 不会重启原始 timeout。到期错误为 `DeadlineExceeded`，调用方显式取消为 `Cancelled`。`demo/Timeout` 和 `demo/Cancel` 展示两种终止路径。

## Metadata

Metadata 是 RPC envelope state，不是业务合同参数。需要由调用方为某一次 invocation 明确选择 metadata 时，使用窄能力 `GetWithMetadata<TContract>(SharpLinkMetadata)` 获取绑定该 metadata 的 proxy，再正常调用业务方法；它不会恢复通用 options bag，也不会使用 ambient/global state。例如：

```csharp
var tenantProxy = client.GetWithMetadata<IMyService>(
    new SharpLinkMetadata(new("tenant", "tenant-a")));
await tenantProxy.GetAsync(id, cancellationToken);
```

Client interceptor 仍可通过 `SharpLinkClientInvocationContext.Metadata` 为横切策略补充/变换当前逻辑调用的 metadata；不要用调用顺序或全局可变 interceptor 状态模拟 caller-selected metadata。服务端从 `SharpLinkCallContext.Current?.Metadata` 或 Server interceptor context 读取。Metadata 受 `MaxMetadataBytes` 限制，适合低基数路由/诊断信息，不适合大对象、凭据日志或无限增长标签。

## Streaming

流式 item 逐项编码，受 stream 和 connection 两级字节窗口约束。消费者只有实际取走 item 后才归还 credit；慢消费者因此限制发送方，而不是无限缓冲。每个接收 dispatcher 最多缓存 4096 个元素，此外仍受字节窗口和 frame 上限约束。

提前停止消费时必须释放异步枚举器。Client 会发送 `ConsumerAbandoned` Cancel，关闭本地 stream、归还已消费 credit，并抑制迟到响应。不要把返回的 `IAsyncEnumerable<T>` 交给多个并发消费者；框架只支持单消费者。

## ClientStreaming 与 Duplex

生成代理在请求被接受后启动客户端流泵。请求与每个 stream 使用同一连接；重连或 retry 不会跨连接迁移已开始的流。Streaming 和 OneWay 不自动 retry，只有标注 `[Idempotent]` 的 Unary 才进入 retry policy。

## OneWay

OneWay 成功表示请求已进入本地发送/服务端接收流程，不包含业务结果。服务端接入控制默认不排队 OneWay；超限时丢弃并记录指标。只有明确设置 `QueueOneWayCalls` 才允许等待。业务失败只能通过服务端日志、Activity、指标或应用补偿观察。

## `[NonCancellable]`

没有 `CancellationToken` 的 RPC 必须显式标注 `[NonCancellable]`。调用方取消后不再等待并清理框架资源，但服务实现可能继续运行；适用于确实不可取消、可接受后台完成的短任务。长任务应接收 token 并及时观察。

完整调用形态见 `demo/Streaming`、`demo/Oneway`、`demo/Cancel` 和 `demo/Timeout`。
