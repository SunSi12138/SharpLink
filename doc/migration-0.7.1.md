# 迁移到 SharpLink 0.7.1

0.7.1 将 RPC 根服务注册改为 Generator Manifest 自动注册，并增加运行时程序集安全注册/注销。Protocol v2 wire format 没有改变，但旧 `AddService` API 已全部删除。

## 服务注册

类型注册改为在实现类上声明 `[RpcService]`。Server `Build()` 会快照当时已经加载的生成 Manifest：

```csharp
[RpcService]
public sealed class MyService : IMyService;

var server = SharpLinkServerBuilder.Create()
    .UseTcp(5000)
    .Build();
```

旧 API 与新 API 的对应关系：

| 0.7.0 | 0.7.1 |
| --- | --- |
| `AddService<TContract,TService>()` | 在 `TService` 上添加 `[RpcService]`，默认自动注册 |
| `AddService<TContract>(instance)` | `ReplaceService<TContract>(instance)` |
| `AddService<TContract>(factory, lifetime)` | `ReplaceService<TContract>(factory, SharpLinkServiceLifetime)` |
| 仅注册选定类型 | `DisableAutomaticServiceRegistration()` 后调用 `EnableService<TContract>()` |
| 移除自动发现的服务 | `ExcludeService<TContract>()` |

`EnableService` 找不到生成服务时 Build 失败；`ExcludeService` 找不到目标时无操作。一个 Contract 只能有一个生成服务 Owner，只有当前 Builder 的 `ReplaceService` 可以覆盖它。

## 生命周期与所有权

`RpcServiceAttribute.Lifetime` 和 factory 的默认值均为 `SharpLinkServiceLifetime.Singleton`：

- `Singleton`：每个 Server registration 一个惰性实例，无调用 Scope。
- `Connection`：每条认证成功的物理连接和 registration 一个惰性实例，断连且相关调用完成后释放。
- `Call`：每次调用一个实例和 Scope；Streaming 使用同一实例直到整条流终止。

0.7.0 的 `ServiceLifetime.Scoped`/`Transient` 都表示调用级根服务，迁移为 `SharpLinkServiceLifetime.Call`。如果需要真正的连接级状态，请显式选择 `Connection`。

调用方传入 `ReplaceService(instance)` 的对象始终是 caller-owned Singleton，SharpLink 不释放它。factory 产物由 SharpLink 在声明的生命周期边界释放。普通构造函数依赖继续由 `UseServiceProvider` 或 Hosting 容器解析和管理。

## 生成代码的程序集所有权

Contract 所在程序集现在拥有 Descriptor、Proxy、contract-based Stub 与相关 Codec；Service 所在程序集只拥有 Service Descriptor、Activator、生命周期与显式依赖。每个生成程序集只有一个 Manifest、程序集定位特性和 Module Initializer。

契约包需要正常引用 `SharpLink.Sdk`、`SharpLink.Abstractions`、`SharpLink.Runtime`，并把 `SharpLink.Generator` 作为 analyzer 引用。生成器不再在每个引用方重复生成同一 Artifact。静态冲突尽可能在编译期诊断，Build 仍保留防御性全量验证。

## 运行时插件

Build 后加载的插件必须显式注册到使用其 Artifact 的实例：

```csharp
SharpLinkAssemblyRegistrationResult result = client.RegisterAssembly(contractAssembly);
if (!result.Succeeded)
    Console.Error.WriteLine($"{result.Error!.Code}: {result.Error.Message}");

SharpLinkAssemblyUnregisterResult drained = await client.UnregisterAssemblyAsync(
    contractAssembly,
    TimeSpan.FromSeconds(10),
    cancellationToken);
```

Server 使用相同 API。注册是同步、事务化且非异常风格的：参数、状态、Manifest、兼容性、重复、容量、依赖和冲突问题都通过 `Succeeded=false` 与稳定错误码返回。诊断字段只保存字符串，不会因错误结果强引用插件程序集或类型。

注销先进入 Draining，并继续占有路由；新调用得到 `Unavailable`，已有调用和流有机会完成。超时后框架定点取消该模块；业务代码忽略取消时返回 `ReferencesReleased=false`，随后在计数归零后后台释放。调用者取消只停止等待，不回滚已经开始的 Draining。

依赖程序集必须先注册且后注销。0.7.1 不支持 Contract 热替换或程序集替换。

## NativeAOT

NativeAOT 继续使用纯静态 Manifest，不扫描程序集。运行时 `RegisterAssembly` 返回 `PlatformNotSupported`；没有反射 fallback。`UnregisterAssemblyAsync` 只适用于先前成功动态注册的程序集。
