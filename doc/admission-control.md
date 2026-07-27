# 服务端接入控制

接入控制在请求完整校验后、服务实例创建和业务执行前申请资源。拒绝使用结构化 `ResourceExhausted`，不会关闭健康连接。

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
