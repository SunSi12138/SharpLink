# 服务发现与韧性

## 单 endpoint 连接池

默认每 endpoint 一条连接。`UseConnectionPool` 可设置 `MinConnections` 和 `MaxConnections`，范围 1–64。`ConnectAsync` 建立最小连接数，压力下最多扩展到最大值。Streaming 在开始后固定到一条连接，不跨连接迁移。

## 静态 endpoint

`UseEndpoints` 接收 2–64 个不可变 endpoint 和按地址创建 transport 的 factory。Builder 在 `Build()` 时枚举、复制并验证 endpoint；之后修改原集合或 attribute 字典不会改变 Client。

```csharp
builder
    .UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
    .UseCluster(options =>
    {
        options.MinReadyEndpoints = 2;
        options.MaxConnections = 4;
        options.MaxConnectionsPerEndpoint = 2;
    })
    .UseLoadBalancing(SharpLinkLoadBalancingStrategy.PowerOfTwoChoices);
```

内置策略为 PowerOfTwoChoices、Random、RoundRobin、LeastPending。自定义 selector 必须同步、非阻塞，正常路径不分配，并只返回当前 Ready snapshot 的合法索引。

## 动态 Resolver 与 DNS

`UseEndpointResolver` 接收完整 snapshot，而不是增量事件。新 generation 先冻结并建立连接，再原子发布；旧 generation 排空现有调用后释放。无效 snapshot 不替换 last-good。Resolver watch 必须响应取消并最终完成。

`UseDnsEndpoints` 是内置 TCP DNS resolver。DNS 不是服务注册中心：它没有权重/区域/健康语义，TTL 和 OS resolver 行为也可能不同；需要这些能力时实现显式 Resolver SPI。

## Retry

Retry 默认关闭，只对标注 `[Idempotent]` 的 Unary 生效。`MaxAttempts` 是总尝试数（含首次），范围 1–10。Streaming 与 OneWay 不 retry；业务拒绝和明确参数错误不应通过换 endpoint 重试。

每次 retry 重新选择 endpoint 并受原调用 deadline 约束。backoff、jitter 不能突破剩余 deadline。自定义 `ISharpLinkRetryPolicy` 应只根据 `SharpLinkRetryContext` 返回决定，不执行阻塞 I/O。

## Endpoint admission 与 Circuit Breaker

Endpoint admission 在选择之后、建立物理 attempt 前执行，适合 circuit、区域隔离或自定义容量策略。内置 Circuit Breaker 按 endpoint generation 隔离；旧 generation 排空时状态一起退役。

Breaker 仅统计基础设施结果，Closed 达到最小样本且失败率越界后 Open，等待 `BreakDuration` 后进入 HalfOpen，并限制 probe 数。不要把业务 `PermissionDenied`、`InvalidArgument` 等当 endpoint 健康故障。

## 可运行示例

`demo/Resilience` 建立两个真实 TCP Server，要求两个 endpoint Ready，使用 RoundRobin、Idempotent Retry 和 Circuit Breaker，并证明调用到达两个节点。动态 generation、断连、last-good、retry deadline 和 breaker 退役由 IntegrationTests 覆盖。
