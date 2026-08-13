# 传输与部署

SharpLink 的协议、错误、心跳和生命周期位于 transport 之上。内置 transport 共享同一 `ITransportConnection` 契约，但地址、所有权和平台限制不同。

## TCP 与 TLS

```csharp
// 默认仅监听 loopback，且保持明文，适合本机开发和单机进程间调用。
serverBuilder.UseTcp(19090);
clientBuilder.UseTcp("127.0.0.1", 19090);

// 需要向其他网卡暴露时，显式扩大监听范围。
serverBuilder
    .UseTcp(19090)
    .ListenOnAnyAddress()
    .UseTls(serverTlsOptions);

// 仅在可信网络、反向代理后等受控场景显式允许明文 TCP。
serverBuilder
    .UseTcp(19090)
    .ListenOnAnyAddress()
    .AllowUnencrypted();
```

`UseTcp(port)` 现在默认绑定 `IPAddress.Loopback`。`ListenOnAnyAddress()`、`ListenOn(IPAddress)` 和
`UseTls(...)` 彼此独立；非 loopback 且无 TLS 的 TCP 配置会在 `Build()` 时被拒绝，直到调用
`AllowUnencrypted()` 显式 opt-in。旧的字符串式 `UseTcp(port, ip)` 重载保留兼容，
但新代码应优先使用 typed `IPAddress` 重载或 `ListenOn(address)`。

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

稳定扩展面是 `IClientTransportFactory`、`IServerTransportListener` 和
`ITransportConnection`，不是直接构造 `RpcSession`。每次 Connect/Accept 返回一个独立拥有
的连接，并把所有权转移给 Client/Server；成功返回后，factory/listener 不得再释放该连接。
连接失败发生在返回前时，创建者负责逆序清理已物化的资源。

下面是最小的 stream-backed 连接形状。真实实现还应按项目策略保留并聚合多个 cleanup
异常，但无论某一步是否失败，都必须继续释放其余资源。

```csharp
sealed class CustomTransportConnection : ITransportConnection
{
    private readonly Stream _stream;
    private readonly Lock _disposeGate = new();
    private Task? _disposeTask;

    public CustomTransportConnection(string id, Stream stream)
    {
        Id = id;
        _stream = stream;
        Input = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        Output = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public string Id { get; }
    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public EndPoint? LocalEndPoint => null;
    public EndPoint? RemoteEndPoint => null;

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await Output.CompleteAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await Input.CompleteAsync().ConfigureAwait(false);
            }
            finally
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
```

Session 是 transport 的唯一直接 owner：`Fault`、shutdown 和 `DisposeAsync` 可以竞争，但
transport 的 `DisposeAsync` 只会启动一次，所有显式 Session disposal 等待者观察同一个
结果。EOF 或物理错误应由 transport 完成/fault Input/Output 暴露；不要再维护第二个
`isConnected` 回调。不要让多个 Client/Server 隐式共享一个可释放 factory/listener。

## 可运行矩阵

`demo/TransportMatrix` 在一个进程内依次完成 TCP、NamedPipe、UDS（平台支持时）、SharedMemory 和 AnonymousPipe 请求。跨进程所有权见 `demo/SeparatedServer`/`SeparatedClient`；TLS、断连和平台异常场景由 IntegrationTests 覆盖。

## NativeAOT

发布前对实际应用入口执行 NativeAOT，而不只编译类库。契约、DTO、Adapter 与动态模块必须保持生成器可发现；NativeAOT 不支持运行时加载未知插件程序集，动态模块模式适用于 JIT 部署。
