# 调用、流式与取消

## 超时与 deadline

Client 默认请求超时为 30 秒。有效 deadline 取调用方 token、`SharpLinkCallOptions`、Client 默认超时和方法 `[Timeout]` 中最早者。可用 `UseRequestTimeout` 修改默认值，或 `DisableRequestTimeout` 关闭默认值；显式 deadline 和方法 timeout 仍生效。

超时在 wire 上使用绝对 deadline，但本地调度和心跳使用 monotonic clock。到期错误为 `DeadlineExceeded`，调用方显式取消为 `Cancelled`。`demo/Timeout` 和 `demo/Cancel` 展示两种终止路径。

## Metadata

`SharpLinkCallOptions.Metadata` 随请求发送，受 `MaxMetadataBytes` 限制。服务端可从 `SharpLinkCallContext.Current?.Metadata` 或 Interceptor context 读取。Metadata 适合低基数路由/诊断信息，不适合大对象、凭据日志或无限增长标签。

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
