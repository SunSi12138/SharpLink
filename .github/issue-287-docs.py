from pathlib import Path
import re

Path('doc/calls-and-streaming.md').write_text('''# 调用、流式与取消

## 超时、RpcDeadline 与 TimeBudget

Client 默认请求超时为 30 秒，可用 `UseRequestTimeout` 修改默认值，或用 `DisableRequestTimeout` 关闭默认值。方法 `[Timeout]` 是方法级策略，会覆盖 Client 默认 fallback；例如 Client 默认 30 秒、方法 `[Timeout(120)]` 时，该方法的本地策略为 120 秒，而不是两者取最小值。参数less `[Timeout]` 继续表示使用 Client 默认策略。

Runtime 将选中的 `Timeout` 解析为进程本地、基于 monotonic clock 的 `RpcDeadline`。请求真正发出前再计算剩余 `TimeBudget` 并写入 wire；Server 收到后用自己的 monotonic clock 解析新的本地 `RpcDeadline`。因此 Client/Server 不依赖墙钟同步，wire 也不再传播绝对 UTC deadline。

当服务处理一个已有上游 `TimeBudget` 的 RPC 并继续发起下游 RPC 时，上游剩余 lifetime 是真正的上限：先选择下游方法/Client 的本地 timeout policy，再用父调用的剩余 `TimeBudget` 做 cap。中间 hop 不会重启原始 timeout。到期错误为 `DeadlineExceeded`，调用方显式取消为 `Cancelled`。`demo/Timeout` 和 `demo/Cancel` 展示两种终止路径。

## Metadata

Metadata 是 RPC envelope state，不是业务合同参数。Client interceptor 可通过 `SharpLinkClientInvocationContext.Metadata` 为当前逻辑调用提供 metadata；服务端从 `SharpLinkCallContext.Current?.Metadata` 或 Server interceptor context 读取。Metadata 受 `MaxMetadataBytes` 限制，适合低基数路由/诊断信息，不适合大对象、凭据日志或无限增长标签。

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
''')

p = Path('doc/observability.md')
text = p.read_text()
text = text.replace(
    '`ISharpLinkClientInterceptor` 在逻辑调用层执行，可修改 `SharpLinkCallOptions`、添加 metadata、短路调用或观察结果。',
    '`ISharpLinkClientInterceptor` 在逻辑调用层执行，可通过 `SharpLinkClientInvocationContext.Metadata` 添加或替换 envelope metadata、短路调用或观察结果。')
p.write_text(text)

p = Path('doc/architecture.md')
text = p.read_text()
text = text.replace(
    '`IService` / `RpcContract` / `RpcService` / `Oneway` / `Timeout` / `SharpLinkCallOptions`',
    '`IService` / `RpcContract` / `RpcService` / `Oneway` / `Timeout`')
text = text.replace(
    '将当前 `sessionId + requestId + method descriptor + peer + 认证上下文 + deadline + metadata` 挂入 `SharpLinkCallContext`',
    '将当前 `sessionId + requestId + method descriptor + peer + 认证上下文 + 本地 RpcDeadline + metadata` 挂入 `SharpLinkCallContext`')
text = text.replace(
    'Retry 默认关闭，只对显式 `[Idempotent]` Unary 生效；拦截器按 logical call 执行一次，每次 attempt 重新选择 endpoint 并共享入口冻结的绝对 deadline。',
    'Retry 默认关闭，只对显式 `[Idempotent]` Unary 生效；拦截器按 logical call 执行一次，每次 attempt 重新选择 endpoint，并共享逻辑调用入口解析的本地 monotonic `RpcDeadline`；每次真正发包时重新计算剩余 `TimeBudget`。')
text = text.replace(
    '调用侧 `CancellationToken`、monotonic deadline 或 stream consumer early-break',
    '调用侧 `CancellationToken`、本地 monotonic `RpcDeadline` 或 stream consumer early-break')
p.write_text(text)

p = Path('doc/protocol-v2.md')
text = p.read_text()
text = text.replace(
    '当前 protocol minor 为 3，能力包含 metadata、compression、flow control、health check 和 cancellation reason。minor 取双方较小值；1.0.0 只承诺与采用相同 minor-3 握手布局的对端互操作。',
    '当前 protocol minor 为 4，能力包含 metadata、compression、flow control、health check 和 cancellation reason。minor 4 是 `TimeBudget` wire 语义的破坏性边界；低于 4 的 peer 在握手阶段以 `Unimplemented` 拒绝，不会把旧 absolute-deadline 字段按新 duration 解释。')
text = text.replace(
    '`Request` | 非 0 | `contractId:uint64 + methodId:uint64`，随后是可选 deadline、metadata 和业务 payload',
    '`Request` | 非 0 | `contractId:uint64 + methodId:uint64`，随后是可选 TimeBudget、metadata 和业务 payload')
text = text.replace(
    '- `HasDeadline`：Request 路由前缀后包含绝对 UTC deadline（Unix milliseconds，`int64`）。\n- `HasMetadata`：deadline 后包含 `varuint length + metadata bytes`；metadata payload 为 `entryCount:varuint`，随后重复 UTF-8 key/value 的 `varuint length + bytes`。',
    '- `HasTimeBudget`：Request 路由前缀后包含发送瞬间剩余 RPC lifetime（`TimeSpan.Ticks`，非负 `int64`）；它是 duration，不是 UTC timestamp。\n- `HasMetadata`：TimeBudget 后包含 `varuint length + metadata bytes`；metadata payload 为 `entryCount:varuint`，随后重复 UTF-8 key/value 的 `varuint length + bytes`。')
text = text.replace('minor 3 的 `HandshakeRequest`', 'minor 4 的 `HandshakeRequest`')
text = text.replace(
    '压缩只覆盖 Generated Codec 产生的业务 payload，路由、deadline、metadata 和 stream ID 始终保持未压缩，',
    '压缩只覆盖 Generated Codec 产生的业务 payload，路由、TimeBudget、metadata 和 stream ID 始终保持未压缩，')
text = text.replace(
    'Request    = route/deadline/metadata envelope + originalBodyLength:uint32 + compressedBody',
    'Request    = route/TimeBudget/metadata envelope + originalBodyLength:uint32 + compressedBody')
p.write_text(text)

p = Path('README.md')
text = p.read_text()
pattern = re.compile(
    r'契约方法可以在尾部声明一个 `SharpLinkCallOptions`.*?`DisableRequestTimeout\(\)` 只关闭客户端默认值，显式 deadline、`Timeout` 和 `\[Timeout\]` 仍然生效。',
    re.S)
replacement = '''RPC 业务契约只声明业务 payload、流参数以及用于协作取消的 `CancellationToken`；调用控制不再通过 `SharpLinkCallOptions` 伪参数进入方法签名。Metadata 等 envelope state 可由 Client interceptor 的 `SharpLinkClientInvocationContext.Metadata` 提供，Server 从 `SharpLinkCallContext` 读取。

请求 lifetime 使用分层语义：Client 默认 `Timeout` 是 fallback，方法 `[Timeout]` 可覆盖它；Runtime 把选中的 policy 解析为本地 monotonic `RpcDeadline`，并在真正发送 Request 前写入剩余 `TimeBudget`。Server 根据该 duration 创建自己的本地 deadline，跨机器不比较绝对墙钟。已有父 RPC 的剩余 `TimeBudget` 会限制下游调用，避免中间 hop 重启 lifetime。`DisableRequestTimeout()` 只关闭 Client 默认 fallback；方法 `[Timeout]` 和继承的父 lifetime 仍然生效。'''
text, count = pattern.subn(replacement, text, count=1)
assert count == 1, count
p.write_text(text)
