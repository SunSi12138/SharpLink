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

Coordinator 的 `ConnectAsync` 有界并行连接所有 slot。一个 slot 的连接/Resolver/Breaker 状态不与其他 slot 共享。`Get<TContract>` 根据 immutable route snapshot 选择唯一 slot；缺失或冲突路由立即失败，不做猜测或广播。

## 运行时 slot 生命周期

Client 包为 `ISharpLinkMultiClusterClient` 提供完整 slot mutation 扩展。配置委托仍然是普通
`SharpClientBuilder`，因此 TCP、UDS、NamedPipe、SharedMemory、自定义 transport、静态 endpoints、
DNS/dynamic resolver、连接池、负载均衡、认证、重试和拦截器均按普通子客户端规则冻结：

```csharp
await client.AddClusterAsync("search",
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

await client.ReplaceClusterAsync("search",
    child => child.UseDnsEndpoints(
        "search.internal",
        5201,
        SharpLinkTransportFactories.Sockets()),
    TimeSpan.FromSeconds(30),
    cancellationToken);

SharpLinkClusterRemovalResult removal = await client.RemoveClusterAsync(
    "search", TimeSpan.FromSeconds(30), cancellationToken);
```

三个操作串行化，但构建、连接和旧资源清理都不持有 coordinator 的同步锁。状态语义如下：

| Coordinator 状态 | Add | Replace | Remove |
| --- | --- | --- | --- |
| `Created` | 构建并发布未连接 slot | 原子替换未连接 slot | 撤销快照并释放 slot |
| `Connecting` | 拒绝并返回 busy 状态异常 | 拒绝 | 拒绝 |
| `Ready` / `Degraded` | 候选连接成功后发布 | 候选连接、动态 registration 迁移完成后切换 | 先撤销 route，再停止旧 child |
| `Draining` / `Stopped` / `Faulted` | 拒绝 | 拒绝 | 拒绝 |

公开的 clusters、routes 和稳态连接预算属于同一个不可变快照，并通过一次原子写入发布。
候选构建、连接、Manifest/route 冲突或预算检查失败时，候选会停止并释放；旧快照保持不变。
`MaxClusters` 与 `MaxTotalConfiguredConnections` 同样约束运行时操作。为允许零停机 Replace，单个串行
过渡允许的物理配置预算上限为稳态上限的两倍；尚未完成的旧 child cleanup 也计入该上限。

调用方 cancellation 在快照提交前会取消候选等待并触发回滚。提交后 cancellation 只取消调用方等待；
已经发布的快照不会回滚，旧资源清理继续由 coordinator 跟踪。`RemoveClusterAsync` 的
`ReferencesReleased` 表示旧 child 是否在 `gracefulTimeout` 内完成释放；超时会返回
`ForcedStop = true`，停止与释放仍在后台继续。

### Proxy 与 endpoint 语义

- Add 后新 `Get<T>()` 选择新 slot。
- Replace 后新 `Get<T>()` 绑定新 child；Replace 前缓存的 Proxy 继续绑定旧 child，并在旧 child
  开始停止后拒绝新调用。
- Remove 后新 `Get<T>()` 立即失败；旧 Proxy 最终观察到 child 已停止。
- 不自动重绑定旧 Proxy，因此 RPC 热路径没有 coordinator 查询或额外的 slot indirection。
- `UseCluster(...)` 只配置一个 child 内部的多 endpoint pool，不代表 coordinator slot。
- DNS/dynamic resolver 自身的 endpoint 更新不需要 Replace；冻结的静态 endpoint、transport、
  pool 或负载均衡配置变化使用 `ReplaceClusterAsync`。

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

运行时 slot 的 unit 与真实 TCP 证据位于 `SharpLinkMultiClusterClientTests` 和
`RuntimeMultiClusterIntegrationTests`，覆盖 Created/Ready 状态、connect-before-publish、失败回滚、
预算、Proxy 一次绑定、Add/Replace/Remove 和删除后的资源释放结果。
