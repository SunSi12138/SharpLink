# SharpLink 0.8.39 迁移指南

English: [`en/migration-0.8.39.md`](en/migration-0.8.39.md)

0.8.39 不改变合法 Protocol v2 framing、route hash 或 payload wire layout。可观察变化集中在 interceptor 误用和 malformed peer request 的错误分类。

## Interceptor 契约

响应型 Server interceptor 必须调用其 `next` continuation；公共 API 没有让 interceptor 自行填充响应 payload 的入口，因此过去直接返回产生的“空成功”从来不是有效短路。OneWay 没有响应，仍可直接返回。服务调用失败时，Server interceptor 的 `catch` 现在能立即看到 Context 的 `Failed`/`Cancelled`、error code 和 exception。

Client interceptor 短路必须返回与调用形状匹配的 `SharpLinkClientInvocationResult`：Unary/ClientStreaming 为准确响应类型，ServerStreaming/DuplexStreaming 为准确的 `IAsyncEnumerable<T>`，OneWay 必须为 null。错误形状仍抛 `InvalidCastException`，但 Context 现在正确记录 `Failed`。

## Request wire 错误

generated request Codec、Server Stub decoder 和 `RpcEmptyRequestCodec` 对非法 Boolean marker、截断、非法长度、required null 或尾随数据返回 `SharpLinkErrorCode.DataLoss`。若监控过去把这些 peer-controlled 错误归类为 `Internal`，应迁移到 DataLoss。业务 Codec 或服务代码主动抛出的普通 `InvalidDataException` 不受影响，默认仍隐藏为 `Internal`。

框架消费 ClientStreaming/DuplexStreaming 输入时不再捕获调用方 `SynchronizationContext`；这只移除框架 continuation 的回投，不改变应用异步迭代器内部显式选择的调度语义。
