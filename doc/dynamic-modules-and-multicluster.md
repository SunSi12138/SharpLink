# 多集群与动态模块

## 静态多集群路由

多集群 Client 是一个 coordinator，内部拥有多个相互隔离的普通 Client slot。契约程序集通过 assembly attribute 路由：

```csharp
[assembly: SharpLinkClusterContractAssembly("orders", typeof(IOrdersService))]
```

```csharp
var client = SharpLinkMultiClusterClientBuilder.Create()
    .AddCluster("orders", child => child.UseTcp("127.0.0.1", 19091))
    .AddCluster("payments", child => child.UseTcp("127.0.0.1", 19092))
    .Build();
```

路由粒度是“拥有契约的程序集”，不是单个接口。一个契约程序集只能静态归属一个 cluster；需要不同目的地时拆分契约程序集。`demo/MultiCluster` 用两个独立契约项目证明 orders/payments 路由。

默认最多 16 个 slot、总配置连接预算 64、并发 Connect slot 4；可配置上限分别为 256、16384、64。没有静态路由的 slot 必须显式 `AllowDynamicContracts`。

## 生命周期隔离

Coordinator 的 `ConnectAsync` 有界并行连接所有 slot。一个 slot 的连接/Resolver/Breaker 状态不与其他 slot 共享。`Get<TContract>` 根据 immutable route snapshot 选择唯一 slot；缺失或冲突路由立即失败，不做猜测或广播。

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
