# SharpLink 0.7.7 Logical Call、Attempt 与 Retry 设计

0.7.7 在 0.7.6 的固定和动态 endpoint topology 之上增加显式、受限的客户端 Retry。它不改变 Protocol v2 wire format 或握手 capability，也不改变未启用 Retry 的固定单 endpoint 调用路径。

## 启用与安全边界

Retry 默认关闭。通过 Builder 显式启用内置策略或自定义策略：

```csharp
var client = SharpClientBuilder.Create()
    .UseTransport(transport)
    .UseRetry(options =>
    {
        options.MaxAttempts = 3; // 包括首次调用
        options.InitialBackoff = TimeSpan.FromMilliseconds(50);
    })
    .Build();
```

无论配置了哪个 policy，Core 都只允许 `RpcMethodKind.Unary && IsIdempotent` 自动重试。也就是说，契约方法必须显式标记 `[Idempotent]`；OneWay、客户端/服务端/双向 Streaming 和未标记的 Unary 永远只发起一次调用。此限制不能由自定义 policy 绕过，因为重试包装器只在 generated descriptor 已满足该条件时进入。

`MaxAttempts` 是总 attempt 数，范围 1–10，默认值是 3。内置策略只接受 `Unavailable` 或 `ConnectionClosed`，涵盖 endpoint 选择失败、断连、发送失败、连接切换和远端 `Unavailable`。`ResourceExhausted`、认证/授权、参数或业务错误、用户取消和 deadline 都不会被默认策略重试。远端 `Unavailable` 仍可能意味着服务端已实际执行，因此 `[Idempotent]` 是调用方的安全承诺。

## Logical call 与 attempt

Unary 调用先一次性解析 CallOptions、metadata 和 absolute deadline，再执行一次 Client Interceptor 链。Retry 位于 interceptor terminal 内部；每一个 attempt 只运行 endpoint/connection selection、PendingCall 注册、发送和完成等待，绝不重新执行 interceptor。

所有 attempt 共用第一次进入 logical call 时计算的绝对 deadline。退避 delay 在 deadline 外会直接以 `DeadlineExceeded` 结束；调用方的取消 token 会同时取消 delay 和后续 attempt。请求 payload 不会为未来的 Retry 提前复制，每个真的 attempt 仍可直接按 codec 序列化。

未配置 Retry、或方法不符合安全边界时，Unary 继续调用原来的 `InvokeUnaryCoreAsync` 直接快速路径，不创建 attempt state、mask 或 delay。

## 完成结果与 endpoint 选择

Retry attempt 使用现有 `PendingRequestTable` 的 compare/exchange 终结仲裁。一个内部 completion observer 随同该 pending entry 生命周期存在，因而 Response、RemoteError、ConnectionClosed、SendFailure、deadline 与 cancel 的竞争仍只会有一个完成者；observer 在 PendingCall 归还对象池前清除。每次 attempt 的内部结果包含 endpoint ID/generation、connection ID、完成原因、是否收到合法 Response/Error、错误码和 elapsed。

多 endpoint 调用在 logical call 内维护一个 `ulong` exclusion mask。每次 attempt 重新读取当前 immutable ready snapshot，并优先排除已选 endpoint。64 个候选均已尝试后，若 policy 与预算仍允许，mask 才清零并复用候选；动态 resolver 发布了新 generation 时 snapshot 引用改变，mask 自动从新候选状态重新开始。单 endpoint 场景可以在连接恢复后重试同一 endpoint。

## 自定义 Retry policy 与遥测

`ISharpLinkRetryPolicy.Evaluate(in SharpLinkRetryContext)` 同步返回 `SharpLinkRetryDecision`。policy 不应阻塞、执行 I/O 或直接发起 attempt。负 delay 或 policy 抛异常将以 `FailedPrecondition` 终止当前 logical call，Client 和连接保持可用。

正常 telemetry 仍以 logical call 为单位记录一次。存在 `SharpLink.Client` Activity listener 时，每个网络 attempt 另产生一个 `sharplink.rpc.attempt` Activity，并带固定的 contract、method、kind、attempt number 标签；逻辑调用 counters 不会因重试而重复计数。endpoint ID 和 connection ID 保留在内部 outcome/诊断路径，不进入默认 Meter 标签。

## 验证范围

0.7.7 Unit 覆盖 remote `Unavailable`、response observation、non-idempotent/`ResourceExhausted` 拒绝、共享 absolute deadline、delay cancellation、custom policy 的非法 delay、interceptor 一次执行以及两 endpoint exclusion/reset。全量验证继续覆盖 PackageSmoke、NativeAOT 和未启用 Retry 的性能门禁。
