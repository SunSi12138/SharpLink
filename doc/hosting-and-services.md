# Hosting 与服务生命周期

## Generic Host

`AddSharpLinkServer` 和 `AddSharpLinkClient` 注册 builder、HostedService 与 readiness health check：

```csharp
services.AddSharpLinkServer(builder => builder.UseTcp(19090));
services.AddSharpLinkClient(builder => builder.UseTcp("127.0.0.1", 19090));
```

Host 启动 Client/Server，停止时执行有界排空和异步释放。通过 `ISharpLinkClientAccessor.GetClientAsync` 等待 hosted Client；不要在容器构建期间同步阻塞获取连接。

健康检查名称默认是 `sharplink_server` 和 `sharplink_remote`，tag 为 `ready`。Server readiness 表示接收路径已启动；remote readiness 表示 Client 可用，不保证某个具体业务依赖健康。

## 自动服务注册

Generator 为 `[RpcService]` 产生 Manifest。Server `Build()` 获取 immutable snapshot，并按 contract id 注册服务。默认自动暴露当前 Manifest 中的服务；可用：

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

停止顺序：停止接受新连接/调用，发送 GoAway，排空活动调用和流，关闭 session 与后台 loop，释放服务和 transport。强制超时后仍会清理 framework state，并通过指标/日志报告未完成调用。应用必须观察 `RunAsync`/HostedService 的终止异常。

## AnonymousPipe Hosting

若 Server transport 实现 `IAnonymousPipeAllocator`，Hosting 注册 `IAnonymousPipeAllocatorAccessor`，供父进程服务安全生成一次性子进程句柄。不要把 allocator 或 offer 暴露给不可信调用方。

完整用法见 `demo/HostApplication`。
