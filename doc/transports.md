# 传输与部署

SharpLink 的协议、错误、心跳和生命周期位于 transport 之上。内置 transport 共享同一 `ITransportConnection` 契约，但地址、所有权和平台限制不同。

## TCP 与 TLS

```csharp
serverBuilder.UseTcp(19090, "0.0.0.0");
clientBuilder.UseTcp("127.0.0.1", 19090);
```

TLS 在 SharpLink 握手前完成，拥有独立的 TLS handshake timeout。Client 默认保留系统证书验证；不要在生产中用总是返回 true 的回调。多 endpoint TLS factory 会复制认证选项，并优先使用 endpoint `Authority` 作为 SNI/TargetHost。

## Unix-domain socket

UDS 适合同机 Unix 进程。路径生命周期属于部署者；异常退出可能留下 socket 文件，重启脚本应只清理自己拥有且确认无监听者的路径。运行前检查 `Socket.OSSupportsUnixDomainSockets`。

## NamedPipe

NamedPipe 适合同机 IPC。Windows 地址包含 server name 和 pipe name；其他受支持平台使用 .NET NamedPipe 实现。逻辑 pipe name 禁止路径语法，避免把名称误当文件路径。

## AnonymousPipe

Server 通过 `IAnonymousPipeAllocator.AllocateAsync` 创建一次性 offer，再把两个句柄安全传给子进程。句柄是凭据：不要记录、复用或放入异常文本。子进程继承后，父进程调用 `CompleteHandleTransfer` 关闭本地 client-handle 副本；同进程测试应保持 offer 到 client 释放。

AnonymousPipe 不支持自动重连或多 endpoint 池。每个新连接都需要新 offer。

## SharedMemory

SharedMemory 是显式选择的同用户、同机器传输，数据走两个有界 ring，控制通道负责握手和通知。它不是跨机器协议，也不是持久化队列。名称映射和控制端点必须由同一安全主体访问。

每方向容量必须是 64 KiB 到 256 MiB 的 2 次幂；默认按 Profile 为 LowLatency 1 MiB、Balanced 8 MiB、Throughput 32 MiB。无法直接写入 ring 的帧通过有界 spill/staging 路径处理，仍受 send queue 和 protocol frame 上限约束。

## 自定义 transport

实现 `IClientTransportFactory`、`IServerTransportListener` 和 `ITransportConnection`。每次 Connect/Accept 返回独立拥有的连接；Dispose 必须停止 I/O、完成 pipelines 并可重复调用。不要让多个 Client/Server 隐式共享一个可释放 factory/listener。

## 可运行矩阵

`demo/TransportMatrix` 在一个进程内依次完成 TCP、NamedPipe、UDS（平台支持时）、SharedMemory 和 AnonymousPipe 请求。跨进程所有权见 `demo/SeparatedServer`/`SeparatedClient`；TLS、断连和平台异常场景由 IntegrationTests 覆盖。

## NativeAOT

发布前对实际应用入口执行 NativeAOT，而不只编译类库。契约、DTO、Adapter 与动态模块必须保持生成器可发现；NativeAOT 不支持运行时加载未知插件程序集，动态模块模式适用于 JIT 部署。
