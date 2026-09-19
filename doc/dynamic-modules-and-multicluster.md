# 多集群与动态模块

## 静态多集群路由

多集群 Client 是一个 coordinator，内部拥有多个相互隔离的普通 Client slot。契约程序集通过 assembly attribute 路由：

```csharp
[assembly: SharpLinkClusterContractAssembly("orders", typeof(IOrdersService))]
```

```csharp
var client = SharpLinkMultiClusterClientBuilder.Create()
    .UseRequestTimeout()
    .AddCluster("orders", child => child.UseTcp("127.0.0.1", 19091))
    .AddCluster("payments", child => child.UseTcp("127.0.0.1", 19092))
    .Build();
```

Coordinator 也必须显式选择 child Client 的 request-timeout policy。`UseRequestTimeout()` 使用推荐的 30 秒 Unary fallback，`UseRequestTimeout(timeout)` 使用自定义 fallback，`DisableRequestTimeout()` 明确关闭 fallback；slot 配置委托仍可为该 child 显式覆盖 coordinator policy。运行时 Add/Replace 同样继承当前 coordinator policy，除非对应 child 配置覆盖它。

路由粒度是“拥有契约的程序集”，不是单个接口。一个契约程序集只能静态归属一个 cluster；需要不同目的地时拆分契约程序集。`demo/MultiCluster` 用两个独立契约项目证明 orders/payments 路由。

默认最多 16 个 slot、总配置连接预算 64、并发 Connect slot 4；可配置上限分别为 256、16384、64。没有静态路由的 slot 必须显式 `AllowDynamicContracts`。

## 生命周期隔离

Coordinator 的 canonical lifecycle 是 `StartAsync / WaitForReadyAsync / WaitForShutdownAsync / StopAsync`。`StartAsync` 只启动本地 child runtime，不把远端 readiness 混入生命周期；`WaitForReadyAsync()` 可以等待当前所有 slot，`WaitForReadyAsync(cluster)` 可以只等待指定 slot。兼容 `ConnectAsync` 仍提供 legacy all-required-slots connection operation，但不会定义 coordinator lifecycle。

一个 slot 的连接/Resolver/Breaker 状态不与其他 slot 共享。`Get<TContract>` 根据 immutable route snapshot 选择唯一 slot；缺失或冲突路由立即失败，不做猜测或广播。

## 运行时 slot 生命周期

Client 包为 `ISharpLinkMultiClusterClient` 提供完整 slot mutation 扩展。配置委托仍然是普通
`SharpClientBuilder`，因此 TCP、UDS、NamedPipe、SharedMemory、自定义 transport、静态 endpoints、
DNS/dynamic resolver、连接池、负载均衡、认证、重试和拦截器均按普通子客户端规则冻结。

Add、Replace、Remove 分别返回自己的 immutable structured result；预期的 control-plane/domain rejection 通过稳定的 `SharpLinkClusterMutationFailureCode` 表达，不要求调用方解析异常文本：

```csharp
SharpLinkClusterAddResult add = await client.AddClusterAsync(
    "search",
    child => child
        .UseTcp("127.0.0.1", 5201)
        .UseConnectionPool(pool =>
        {
            pool.MinConnections = 2;
            pool.MaxConnections = 8;
        })
        .UseRetry(),
    slot => slot.AllowDynamicContracts = true,
    cancellationToken);

if (!add.Succeeded)
{
    switch (add.FailureCode)
    {
        case SharpLinkClusterMutationFailureCode.AlreadyExists:
        case SharpLinkClusterMutationFailureCode.Busy:
        case SharpLinkClusterMutationFailureCode.LifecycleClosed:
        case SharpLinkClusterMutationFailureCode.RouteConflict:
        case SharpLinkClusterMutationFailureCode.CapacityExceeded:
            // 预期 control-plane rejection；Message 只用于诊断，不应作为分支条件。
            break;
    }
}

// Add 成功只表示本地 publication 已提交；需要立即 RPC 时显式等待该 slot Ready。
await client.WaitForReadyAsync("search", cancellationToken);

SharpLinkClusterReplacementResult replacement = await client.ReplaceClusterAsync(
    "search",
    child => child.UseDnsEndpoints(
        "search.internal",
        5201,
        SharpLinkTransportFactories.Sockets()),
    TimeSpan.FromSeconds(30),
    cancellationToken);

if (!replacement.Succeeded &&
    replacement.FailureCode == SharpLinkClusterMutationFailureCode.CandidateUnavailable)
{
    // replacement 未发布，旧 cluster 仍然是 authoritative generation。
}
else if (replacement.Published && replacement.ForcedStop)
{
    // 新 generation 已提交；旧 child 的 coordinator-owned cleanup 仍在继续。
}

SharpLinkClusterRemovalResult removal = await client.RemoveClusterAsync(
    "search", TimeSpan.FromSeconds(30), cancellationToken);
```

`SharpLinkClusterMutationFailureCode` 当前公开的稳定 expected rejection 为：

| Code | 含义 |
| --- | --- |
| `AlreadyExists` | Add 的 key 已发布 |
| `NotFound` | Replace/Remove 的 key 不存在 |
| `Busy` | 另一个 lifecycle/control-plane operation 正占有 mutation boundary，或当前处于不允许该 mutation 的过渡状态 |
| `LifecycleClosed` | coordinator 已进入 Draining/Stopped/Faulted，或在 publication 前开始关闭 |
| `RouteConflict` | Add 的 contract route 与已发布 route 冲突 |
| `CapacityExceeded` | cluster 数、稳态连接预算或 bounded transition budget 超限 |
| `CandidateUnavailable` | Replace candidate 在 publication 前无法达到远端可用边界；旧 generation 保持 authoritative |

`None` 只表示没有 expected rejection。`Message` 是人类可读诊断，不是稳定的程序分支 contract。
Programmer error（例如非法参数）、caller cancellation、builder/configuration/manifest 错误以及内部 invariant/unexpected failure 仍然通过异常传播，不会被压成 catch-all result code。

三个操作串行化，但候选构建、远端连接和旧资源清理都不持有 coordinator 的同步锁。状态与事务语义如下：

| Coordinator lifecycle / connectivity | Add | Replace | Remove |
| --- | --- | --- | --- |
| `Created` | 本地构建并发布未启动/未连接 slot | 原子替换未连接 slot | 撤销快照并释放 slot |
| `Starting` 或纯 legacy `Created + Connecting` | `Busy` | `Busy` | `Busy` |
| `Running`，aggregate `Connecting` | 启动 child 后可提交 publication；不等 Ready | `Busy` | `Busy` |
| `Running`，aggregate `Ready / Degraded` | child `StartAsync` 后原子发布；readiness 独立收敛 | candidate `Start/Connect` 成功后才 swap | 先撤销 route，再停止旧 child |
| `Draining / Stopped / Faulted` | `LifecycleClosed` | `LifecycleClosed` | `LifecycleClosed` |

公开的 clusters、routes 和稳态连接预算属于同一个不可变快照，并通过一次原子写入发布。Add 的 publication commit point 是 local validation、candidate start、route/budget revalidation 全部完成之后；它不读取 candidate readiness，因此远端首次 dial/handshake 失败不会把一个已合法提交的 Add 变成失败。`WaitForReadyAsync(cluster)` 是单独的 readiness primitive。

Replace 保持 availability-first：candidate 必须先满足现有 connect/ready 边界才会 swap。若 candidate 无法可用，返回 `CandidateUnavailable` 且 `Published = false`；旧 slot/route 保持不变。若 swap 已提交，则 `Succeeded = true`、`Published = true`，旧 child 是否已在 `gracefulTimeout` 内释放由 `ReferencesReleased` / `ForcedStop` 独立报告。也就是说 `ForcedStop = true` 不等于 replacement rollback。

Remove 的 `Succeeded = true` 表示 slot/route 已从 public snapshot 撤销；`ReferencesReleased` 表示旧 child 是否在 `gracefulTimeout` 内完成释放，`ForcedStop = true` 表示 cleanup 超出 bounded wait、但仍继续由 coordinator 持有。`NotFound`、`Busy`、`LifecycleClosed` 在撤销前返回 structured rejection，不污染快照。

调用方 cancellation 在 Add publication / Replace swap / Remove unpublish 之前仍以 `OperationCanceledException` 传播并保持原 authoritative snapshot；提交后的 cancellation 只取消调用方对 cleanup 的等待，已提交的 mutation 不会回滚，cleanup 继续由 coordinator 跟踪。

### Proxy 与 endpoint 语义

- Add 后新 `Get<T>()` 选择新 slot；若调用要求立即远端可用，应先显式 `WaitForReadyAsync(cluster)`。
- Replace 后新 `Get<T>()` 绑定新 child；Replace 前缓存的 Proxy 继续绑定旧 child，并在旧 child开始停止后拒绝新调用。
- Remove 后新 `Get<T>()` 立即失败；旧 Proxy 最终观察到 child 已停止。
- 不自动重绑定旧 Proxy，因此 RPC 热路径没有 coordinator 查询或额外的 slot indirection。
- `UseCluster(...)` 只配置一个 child 内部的多 endpoint pool，不代表 coordinator slot。
- DNS/dynamic resolver 自身的 endpoint 更新不需要 Replace；冻结的静态 endpoint、transport、pool 或负载均衡配置变化使用 `ReplaceClusterAsync`。

## 动态程序集注册

动态模块由 generated Manifest 描述 contract、service、Codec 和 cluster route。注册流程先验证版本、依赖闭包、contract id、schema/wire identity 和路由所有权，再原子发布 snapshot。

替换不是覆盖字典：新 generation 先完整验证并发布，旧 generation 进入 draining；已开始调用继续使用旧服务/Codec，新的调用路由到新 generation。注销等待 active calls/streams 和 adapter scope 释放，超时不会假装成功。

## AssemblyLoadContext 所有权

要真正卸载插件，插件及其依赖必须位于 collectible `AssemblyLoadContext`，且应用不能保留：

- `Assembly`、`Type`、delegate 或生成 proxy 的强引用；
- service singleton、DI scope 或未完成调用；
- Codec/Adapter scope；
- Route/Manifest snapshot；
- 后台任务或事件订阅。

NativeAOT 不支持运行时加载未知插件，动态模块只适用于 JIT 部署。静态多集群路由和预编译 Manifest 可用于 NativeAOT。

## 验证

动态模块的 runnable 证据位于 `test/SharpLink.DynamicContracts`、`SharpLink.DynamicServices`、`SharpLink.RollbackPlugin` 和 `RuntimeAssemblyIntegrationTests`，覆盖注册、冲突、替换、调用排空、取消、回滚、cleanup failure、弱引用与 collectible ALC 回收。

运行时 slot 的 unit 与真实 TCP 证据位于 `test/SharpLink.UnitTests/Client/SharpLinkMultiCluster*Tests.cs` 和 `RuntimeMultiClusterIntegrationTests`，覆盖 Created/Running 状态、Add publication 与 readiness 解耦、structured expected rejection、Replace ready-before-swap、publication-vs-cleanup 结果、取消/Stop race、预算、Proxy 一次绑定、Add/Replace/Remove 和删除后的资源释放结果。

## 2.0 Generated ABI 和发布门禁

所有契约/服务/插件必须用 2.0 SDK 重新生成 API 4，并携带当前 ABI identity。
locator 在 manifest materialization 之前 fail fast；Catalog 是 generated bootstrap，
应用只通过 `ISharpLinkAssemblyRegistry` 注册、替换和注销。
`ReferencesReleased` 描述框架引用释放，不保证应用自身 Assembly/Type/proxy 引用已清空，
因此 ALC 卸载仍需调用方释放引用并验证回收。版本/identity 与完整公开签名由
[API baseline](../eng/public-api/2.0.0) 和 [版本清单](../eng/release-versions.json) 检查。
