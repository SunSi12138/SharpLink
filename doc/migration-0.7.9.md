# SharpLink 0.7.x endpoint、Retry 与 resiliency 迁移指南

## 从固定单 endpoint 迁移

现有 `UseTransport(...)` 保持原样，是默认且最低开销的模式。它不会自动启动 resolver、构造 endpoint candidate、启用 Retry 或 breaker：

```csharp
var client = SharpClientBuilder.Create()
    .UseTransport(SharpLinkTransportFactories.Sockets(/* ... */))
    .Build();
```

需要静态多 endpoint 时，改用 `UseEndpoints` 和每 endpoint 独立 factory。可选的 P2C、Random、RoundRobin、LeastPending 或 custom selector 只影响多 endpoint：

```csharp
builder.UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
       .UseLoadBalancing(SharpLinkLoadBalancingStrategy.PowerOfTwoChoices);
```

需要动态服务发现时使用 `UseEndpointResolver`；Client owns resolver，Stop/Dispose 时会取消 watch 并精确释放它。TCP DNS 使用 `UseDnsEndpoints`。Consul、Etcd、Nacos、Kubernetes 等 SDK 由应用封装为 resolver，不会引入 SharpLink 核心依赖。

## 传输注意事项

TCP、UDS、NamedPipe 与 SharedMemory 可作为 static/dynamic endpoint transport factory。AnonymousPipe 的 handles 只适合固定单 endpoint；多 endpoint 只能由应用提供能为每个 endpoint generation 独立拥有资源的自定义 factory。endpoint Attributes 仅用于 selector/admission 诊断，不能决定连接关键配置；Address 或 Authority 改变会建立新 generation。

## Retry 与幂等性

Retry 默认关闭。只有显式 `[Idempotent]` Unary 才能被 `UseRetry` 或自定义 retry policy 自动重试。`MaxAttempts` 包括首次 attempt；所有 attempts 共用原始 absolute deadline，用户 cancel 会停止 delay 和后续 attempt。不要把非幂等写入、OneWay 或任何 Streaming 方法标记为可自动重试。

默认策略仅重试 endpoint/connection unavailable 与远端 `Unavailable`，不重试 `ResourceExhausted` 或业务错误。服务端可能在响应丢失前已经执行请求，因此业务必须自行保证 `[Idempotent]` 的语义。

## Breaker 与服务端 Admission 的区别

`UseCircuitBreaker` 是客户端对某个 endpoint generation 是否发起新 attempt 的保护；它不会拒绝服务端已经收到的调用，也不会改变物理 connection 的 Ready 状态。服务端 `UseAdmissionControl` 则限制服务实现的并发、速率与队列。二者可同时使用：Breaker 保护端点可用性，Server Admission 保护服务资源。

使用 `UseEndpointAdmission` 可接入外部健康/zone 策略。策略必须同步且非阻塞；不应执行 I/O、发起 RPC 或持有 connection/session。拒绝可以返回 RetryAfter；Report 异常会被记录而不会篡改业务结果。

## API freeze 与可观测性

0.7.9 固定了 endpoint/address、resolver、selector、Retry 和 admission/breaker 的公共 API 形状。Builder 只接受显式实例，不进行程序集扫描，保持 NativeAOT 可达性。默认 metrics 使用固定低基数标签，绝不包含 endpoint ID、host、authority 或 pipe name；需要逐 endpoint 诊断时使用 Activity/结构化日志，而不是 Meter 标签。
