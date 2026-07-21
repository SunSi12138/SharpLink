# SharpLink 0.7.8 Endpoint Admission 与 Circuit Breaker 设计

0.7.8 为 static 与 resolver-backed endpoint topology 增加客户端 endpoint 级准入。它不同于服务端 Admission Control：服务端策略决定某个 RPC 是否进入服务实现；endpoint admission 决定客户端是否应向某个 Ready endpoint 发起一次网络 attempt。固定 `UseTransport` 模式没有 endpoint topology，不创建 admission state。

## 自定义 admission

使用 `UseEndpointAdmission(ISharpLinkEndpointAdmissionPolicy)` 注册策略：

```csharp
builder.UseEndpointAdmission(new ZoneAndHealthPolicy());
```

`TryAcquire(in SharpLinkEndpointCandidate, in RpcMethodDescriptor)` 是同步、非阻塞的选择路径。它可返回拒绝、opaque token 和可选 `RetryAfter`。拒绝会把当前候选排除并继续 selector 的剩余候选；所有候选都拒绝时调用以 `Unavailable` 结束，`WaitForReady` retry 路径会遵守最早 RetryAfter、absolute deadline、cancel 和 Stop。

Policy 获准的 attempt 将在原有 PendingCall 的唯一终结路径中一次性调用 `Report(in SharpLinkEndpointOutcome, token)`。Response、RemoteError、SendFailure、ConnectionClosed、GoAway、deadline、cancel 与 one-way 本地完成都得到固定分类。TryAcquire 异常或负 RetryAfter 以 `FailedPrecondition` 结束当前调用；Report 异常只记录日志，绝不覆盖已经获得的 RPC 结果或关闭连接。

## 内置 Circuit Breaker

```csharp
builder.UseCircuitBreaker(options =>
{
    options.MinimumThroughput = 20;
    options.FailureRatio = 0.5;
    options.SamplingDuration = TimeSpan.FromSeconds(30);
    options.BreakDuration = TimeSpan.FromSeconds(10);
    options.HalfOpenMaxCalls = 1;
});
```

内置 breaker 是 admission policy 的另一种实现，因此不能与自定义 admission 同时注册。每个 `{endpoint ID, generation}` 拥有独立状态：地址或 authority 替换形成新 generation，并从 Closed 开始；动态 retired generation 排空时会释放其 breaker state。

- **Closed**：在有界滚动时间窗记录 endpoint 可用性样本；达到 MinimumThroughput 且失败比例不低于 FailureRatio 时 Open。
- **Open**：在 BreakDuration 内拒绝新 attempt，但不会修改物理连接状态或从 topology 删除 endpoint。
- **HalfOpen**：到期后按原子 permit 放行最多 HalfOpenMaxCalls 个 probe；成功清空旧窗口并回到 Closed，基础设施失败重新 Open。

Breaker 只计入连接关闭、GoAway、发送故障、远端/本地 Unavailable、远端 ResourceExhausted、DataLoss 和 Internal 等基础设施故障。用户取消、deadline、认证/授权、参数/业务错误与本地 Pending/queue ResourceExhausted 不会打开 breaker。状态推进使用 `Stopwatch` 单调时间，没有每 endpoint timer 或 topology writer lock。

## 与 Retry 的顺序

每个 logical Retry attempt 依次执行：Retry budget → endpoint selector → admission/breaker → connection selection → PendingCall/send → terminal Report → retry decision。admission 拒绝不会发送网络请求；Retry 仍优先选择尚未尝试的 endpoint。breaker 全 Open 不改变 Client 的物理 Ready/Reconnecting 状态，`CheckHealthAsync` 依旧只查询真实 Ready connection。
