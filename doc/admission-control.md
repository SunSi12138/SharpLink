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

## 排队

只有 `MaxQueuedCalls`、`MaxQueuedBytes` 和 `MaxQueueDelay` 都允许时才等待；任何一个边界耗尽都会立即拒绝。排队仍受调用 deadline 和取消 token 约束。队列保留已解码请求字节，因此 count 与 byte 两个边界都必须配置。

OneWay 默认不排队，超限即丢弃并记录 `sharplink.admission.oneway.dropped`；设置 `QueueOneWayCalls` 后才允许等待。由于 OneWay 没有响应，调用方不能从返回值判断服务端是否执行。

## Partition

Partition selector 必须同步、快速、低基数，返回稳定字符串或 null/default partition。配置 `MaxPartitions` 和 `IdleTimeout`，避免用户输入制造无限状态。partition entry 只有空闲并超过 idle timeout 才回收。

## 生命周期与指标

permit 覆盖实际服务执行、异步 continuation 和 terminal cleanup。同步抛错、取消、响应队列失败或 Server Stop 都必须释放 permit。相关指标：active permits、queued calls、rejected calls、queue duration、active partitions。

`demo/AdmissionControl` 使用全局并发 1，证明一个调用执行时三个并发请求都收到 `ResourceExhausted`，随后已接入调用正常完成。