# 服务端接入控制

SharpLink 有两层彼此独立的接入保护：连接在进入 Ready 前先经过 connection admission；完成握手后，RPC 调用再经过 call admission。两层都使用固定、可观测的有界资源，不用用户输入创建无界状态。

## 连接与握手边界

`SharpLinkConnectionAdmissionOptions` 保护 accepted/live connection 与 pre-auth handshake。默认最多保留 1024 个 live connection，同时最多允许 64 个连接处于 TLS / Protocol v2 / authentication handshake。handshake slot 在连接 Ready 时立即释放，connection slot 一直保留到该连接的 terminal cleanup。

```csharp
var server = SharpLinkServerBuilder.Create()
    .UseTcp(5000)
    .UseConnectionAdmission(options =>
    {
        options.MaxConcurrentConnections = 1024;
        options.MaxConcurrentHandshakes = 64;
    })
    .Build();
```

默认 handshake 上限是独立的固定安全边界。如果只把 `MaxConcurrentConnections` 配到 64 以下而没有显式设置 handshake 上限，默认 handshake 上限会自动取更低的 connection bound。需要恢复旧的“没有独立 handshake 上限”行为时必须显式设置 `MaxConcurrentHandshakes = 0`；此时实际 handshake 并发仍受 `MaxConcurrentConnections` 限制。显式正值不能大于 connection bound。

超过任一 connection admission 边界时，已 accept 的连接会立即关闭，不进入后续 TLS/Protocol/auth 生命周期，也不排队。服务启动日志会记录最终生效的 `max_connections` 与 `max_handshakes`；`sharplink.connections.handshakes.active` 和 `sharplink.connections.rejected` 可用于观察当前握手占用和拒绝。

## RPC 调用接入

调用接入控制在请求完整校验后、服务实例创建和业务执行前申请资源。拒绝使用结构化 `ResourceExhausted`，不会关闭健康连接。

## 限制层级

规则按 Global、Contract、Method、Partition 组合；一次调用必须同时取得所有适用 permit。每个 scope 可配置一个并发限制和至多一个速率限制：Token Bucket、Fixed Window 或 Sliding Window。

```csharp
serverBuilder.UseAdmissionControl(options =>
{
    options.Global.UseConcurrency(256);
    options.MaxQueuedCalls = 512;
    options.MaxQueuedBytes = 16 * 1024 * 1024;
    options.MaxQueueDelay = TimeSpan.FromSeconds(2);
    options.AddMethod<IOrders>(nameof(IOrders.SubmitAsync), rule =>
        rule.UseTokenBucket(rate =>
        {
            rate.TokenLimit = 1000;
            rate.TokensPerPeriod = 1000;
            rate.ReplenishmentPeriod = TimeSpan.FromSeconds(1);
        }));
});
```

## 运行时启用和停用

Server 包提供运行时控制入口，可在最初未启用接入控制的服务上原子启用，也可停用当前策略并在之后重新启用：

```csharp
ISharpLinkServer server = serverBuilder.Build();

server.EnableAdmissionControl(options =>
{
    options.Global.UseConcurrency(256);
});

server.DisableAdmissionControl();

server.EnableAdmissionControl(options =>
{
    options.Global.UseConcurrency(256);
});
```

`EnableAdmissionControl` 会先在发布锁之外构造、校验并解析完整候选策略；只有候选完全可用后才原子发布。回调失败、配置校验失败、生成清单解析失败或并发启用失败都不会改变当前发布状态。回调只用于构造候选配置；方法返回后继续修改调用方保留的 options 对象不会改变已发布策略。

支持的状态转换只有 Disabled → Enabled、Enabled → Disabled 和停用后的再次 Disabled → Enabled。已启用时再次调用 `EnableAdmissionControl` 不表示在线修改策略，而会抛出 `InvalidOperationException`；如需切换策略，先显式停用，再重新启用。对已停用状态重复调用 `DisableAdmissionControl` 是幂等操作。不支持这些运行时入口的自定义 `ISharpLinkServer` 实现会抛出 `NotSupportedException`。

停用只影响之后捕获接入状态的请求，不会取消已经捕获旧 generation 的活动或排队请求，也不会等待这些请求结束。旧 generation 会按正常 retire/reclaim 生命周期完成；在旧 generation 尚未回收时以兼容配置重新启用，会复用稳定 kernel 中兼容的并发、速率、队列和 partition 状态，因此不会重置已消费配额或复制全局记账。

普通的 `DisableAdmissionControl` 不是 Server Stop：它只切换 Admission publication，不触发 `StopAccepting`，也不取消或等待旧 generation。反过来，一旦 Server 已进入 Draining、Stopped 或 Faulted，Admission control plane 就已封口；之后的 `EnableAdmissionControl` 或 `DisableAdmissionControl` 都会抛出 `InvalidOperationException`，且不会再发布任何 program。与 Stop 并发时，结果按同一生命周期 writer lock 的线性化顺序决定。

运行时停用 Admission 不会停用 `ServerResourceGovernor`。调用容量、解码/预接入预算、保留字节和流式字节等服务器资源限制始终独立生效。

## 排队

只有 `MaxQueuedCalls`、`MaxQueuedBytes` 和 `MaxQueueDelay` 都允许时才等待；任何一个边界耗尽都会立即拒绝。排队仍受调用 deadline 和取消 token 约束。队列保留已解码请求字节，因此 count 与 byte 两个边界都必须配置。

OneWay 默认不排队，超限即丢弃并记录 `sharplink.admission.oneway.dropped`；设置 `QueueOneWayCalls` 后才允许等待。由于 OneWay 没有响应，调用方不能从返回值判断服务端是否执行。

## Partition

Partition selector 必须同步、快速、低基数，返回稳定字符串或 null/default partition。配置 `MaxPartitions` 和 `IdleTimeout`，避免用户输入制造无限状态。partition entry 只有空闲并超过 idle timeout 才回收。

## 生命周期与指标

permit 覆盖实际服务执行、异步 continuation 和 terminal cleanup。同步抛错、取消、响应队列失败或 Server Stop 都必须释放 permit。相关指标：active permits、queued calls、rejected calls、queue duration、active partitions。

`demo/AdmissionControl` 使用全局并发 1，证明一个调用执行时三个并发请求都收到 `ResourceExhausted`，随后已接入调用正常完成。
