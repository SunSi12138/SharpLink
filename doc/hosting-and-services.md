# Hosting 与服务生命周期

## Generic Host

`AddSharpLinkServer` 和 `AddSharpLinkClient` 注册 builder、HostedService 与 readiness health check：

```csharp
services.AddSharpLinkServer(builder => builder.UseTcp(19090));
services.AddSharpLinkClient(builder => builder
    .UseTcp("127.0.0.1", 19090)
    .UseRequestTimeout());
```

Hosted Client 与直接构建的 Client 一样，必须显式选择 `UseRequestTimeout()`、`UseRequestTimeout(timeout)` 或 `DisableRequestTimeout()`；未指定会在 Host materialize Client 时失败。

Host 启动 Client/Server，停止时执行有界排空和异步释放。Server HostedService 直接映射 `StartAsync / StopAsync`，不再持有独立的 accept-loop task 或 lifetime CTS；长期 accept/background runtime 由 Server 自己持有和观察。Client HostedService 调用 `StartAsync` 启动本地 runtime 与连接 supervisor；它不会等待远端 endpoint ready，因此远端暂时不可用不会阻塞整个 Generic Host 启动。通过 `ISharpLinkClientAccessor.GetClientAsync` 等待 hosted Client 本地 runtime 发布；不要在容器构建期间同步阻塞获取连接。
Server 的 canonical lifecycle 只有 `StartAsync / WaitForShutdownAsync / StopAsync`；public `RunAsync` 已移除。`LifecycleState` 描述 `Created/Starting/Running/Draining/Stopped/Faulted`，而 `HealthStatus` 单独描述本地 serving readiness。`StartAsync` 成功意味着 Server-owned accept infrastructure 已建立且 lifecycle 已发布为 `Running`；完成 startup 后，调用方传入的 startup cancellation token 不再拥有 Server lifetime。

当前内置 socket listener 在 transport 构造时同步完成 bind/listen，因此端口占用、地址无效等 bind failure 会在构造阶段 fail fast；自定义 listener 若在首次 accept startup boundary 立即失败，`StartAsync` 会直接传播该异常。`WaitForShutdownAsync(ct)` 不发起停止，`ct` 只取消当前 waiter；正常 lifetime 只能由显式 `StopAsync` 或不可恢复的 Server-owned runtime failure 终止。

健康检查名称默认是 `sharplink_server` 和 `sharplink_remote`，tag 为 `ready`。Server readiness 表示接收路径已启动；remote readiness 通过协议健康检查表示远端可用，不等同于 Client 的多 endpoint topology readiness，也不保证某个具体业务依赖健康。

## 自动服务注册

Generator 为 `[RpcService]` 产生 Manifest，并在引用它的应用编译中生成确定性的静态 bootstrap。应用模块初始化会先执行这些 assembly-owned bootstrap，随后 Server `Build()` 获取 immutable snapshot，并按 contract id 注册服务。因此 Server 到 Service 的普通 `ProjectReference` 足以注册服务，不需要 marker type、`Assembly.Load`、输出目录扫描或手动 `RegisterAssembly`；Service 实现仍可为 `internal`，该路径兼容 trimming/NativeAOT。默认自动暴露当前 Manifest 中的服务；可用：

- `DisableAutomaticServiceRegistration()`
- `EnableService<TContract>()`
- `ExcludeService<TContract>()`
- `ReplaceService<TContract>(instance)`
- `ReplaceService<TContract>(factory, lifetime)`

替换 instance 由调用方拥有，SharpLink 不释放；内部 factory 创建的 singleton/connection/call 实例按配置生命周期释放。

## DI 与 scope

`UseServiceProvider` 接受应用拥有的 provider。Server 不释放它，但会为 Connection/Call 生命周期创建和释放 scope。开启 `ValidateScopes` 有助于在启动时发现 singleton 捕获 scoped 依赖。

Service lifetime：

- Singleton：Server 实例共享，必须线程安全。
- Connection：每物理连接一个实例，断连后释放。
- Call：每次调用一个实例，开销最高，最易隔离。

## 优雅停止

停止顺序：停止接受新连接/调用，发送 GoAway，排空活动调用和流，关闭 session 与后台 loop，释放服务和 transport。强制超时后仍会清理 framework state，并通过指标/日志报告未完成调用。应用可通过 `WaitForShutdownAsync` 观察真实终态；不可恢复的 Server runtime/cleanup fault 会在 Server-owned cleanup 完成后由该等待传播。

## AnonymousPipe Hosting

若 Server transport 实现 `IAnonymousPipeAllocator`，Hosting 注册 `IAnonymousPipeAllocatorAccessor`，供父进程服务安全生成一次性子进程句柄。不要把 allocator 或 offer 暴露给不可信调用方。

完整用法见 `demo/HostApplication`。
