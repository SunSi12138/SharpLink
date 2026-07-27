# SharpLink 0.8.40 迁移指南

English: [`en/migration-0.8.40.md`](en/migration-0.8.40.md)

0.8.40 不改变合法 Protocol v2 framing、route hash、request schema、manifest wire type 或 payload layout。generated method metadata 新增 response nullability，用于本地生成边界校验。

## Interceptor continuation

调用 `next` 后不得让 terminal 在 interceptor 返回后成为孤儿。框架现在会 join 已调用但未完成的 continuation；直接 `return next(context)` 与正常 `await next(context)` 保持既有异常和结果语义。Client interceptor 仍可不调用 `next` 并返回类型正确的短路结果。响应型 Server interceptor 仍必须调用 `next`；OneWay 可直接返回。

## Response nullability

Generator 现在在 Proxy 与 Stub 签名中保留 nullable reference annotation。`ValueTask<T>`、`Task<T>` 与 `IAsyncEnumerable<T>` 的 non-nullable `T` 不得返回 null；违约服务结果映射为 `Internal`，违约 Client interceptor 短路结果抛 `InvalidCastException`。显式 `T?` 仍允许 null。源码显示变化不会改变方法 ID 或 wire type 查找。

## Error API

`RpcException` 已删除；应用应使用带具体 `SharpLinkErrorCode` 的 `SharpLinkException`。`Unknown` 与未定义 code 不能构造 `SharpLinkException`，自定义 `IRpcExceptionMapper` 必须返回已定义的具体 code。未知 RPC 方法统一返回 `Unimplemented`。

`RpcMethodDescriptor` 新增只读 `ResponseNullable`。原有构造参数保持兼容，新参数位于末尾且有默认值；原九值 Deconstruct 继续可用，另提供包含 response nullability 的十值形状。
