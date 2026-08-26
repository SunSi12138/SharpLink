# 拦截器与可观测性

## Client Interceptor

`ISharpLinkClientInterceptor` 在逻辑调用层执行，可通过 `SharpLinkClientInvocationContext.Metadata` 添加或替换 envelope metadata、短路调用或观察结果。每个 `next` 只能调用一次；返回前必须等待它完成，不能启动后丢弃 `ValueTask`。

Client interceptor 开启后请求值可能装箱，结果通过 `SharpLinkClientInvocationResult` 表示。短路结果必须与生成签名匹配，否则调用以 `Internal` 失败。零 interceptor 时走不装箱的生成快路径。

## Server Interceptor

Server interceptor 可做授权、审计、租户策略或异常边界。带响应的方法必须调用 `next` 或抛出 `SharpLinkException`；OneWay 可以明确短路。返回时 context 提供最终 `Status`、`ErrorCode`、`Exception` 和 `Elapsed`。

Interceptor 按注册顺序进入、逆序退出。不要在单例 interceptor 中保存某次调用 context；context 生命周期只覆盖调用。

## 业务异常

默认未知服务异常映射为 `Internal` 和安全消息。`IRpcExceptionMapper` 可根据 Server context 返回具体 `SharpLinkException`。Mapper 本身抛错不会破坏 session 写路径，框架退回安全 `Internal`。

Mapper 只存在于 Server invocation layer。生成代码通过每连接的窄 bridge 提交流式终态；
`RpcSession` 只编码已经结构化的错误，不持有 mapper，也不解释 service/contract/method policy。

`EnableDetailedErrors` 会把业务异常详情返回给对端，仅用于可信开发环境。

## Activity

ActivitySource：

- `SharpLink.Client`
- `SharpLink.Server`

逻辑调用生成 `sharplink.rpc` activity；retry 的物理 attempt 生成独立 attempt activity，但不会重复逻辑调用计数。标签使用 contract/method id、kind、side、attempt 和低基数终止原因；不要把完整 endpoint、用户 id 或异常文本变成高基数指标标签。

## Meter

Meter 名为 `SharpLink`。核心指标覆盖 calls、duration、pending requests、active streams、connections、bytes、send queue、protocol/auth failures、admission、resolver、retry、breaker 和 shared-memory 路径。

指标只有存在 listener 时才执行记录热路径。部署时通过 OpenTelemetry .NET MeterProvider 订阅 `SharpLink`，通过 TracerProvider 订阅两个 ActivitySource。

## 日志

`LogEvents` 为 connection、RPC、stream、transport、server、client 分配稳定数字区间。结构化模板字段保持固定；不要把 token、anonymous-pipe handle 或未经清理的 metadata 写入日志。

`demo/InterceptorsTelemetry` 注册双端 interceptor 和 ActivityListener，并验证两个调用 activity；`demo/Log` 展示 ILogger 集成。
