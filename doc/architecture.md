# SharpLink 架构说明

## 分层视图

```text
Application
  -> SharpLink.Client / SharpLink.Server / SharpLink.Hosting
    -> SharpLink.Runtime
      -> SharpLink.Abstractions

SharpLink.Sdk
  -> IService / RpcContract / RpcService / Oneway / Timeout / SharpLinkCallOptions

SharpLink.Generator
  -> 扫描契约与服务，生成 Proxy / Stub / Registration

SharpLink.Serializer.MemoryPack
  -> 作为复杂类型的可选编解码兜底
```

## 各模块职责

- `SharpLink.Abstractions`
  - Protocol v2 模型（`ProtocolV2FrameType` / `ProtocolV2FrameFlags` / `ProtocolV2Constants`）
  - 核心抽象（`IRpcChannel`、`IRpcStub`、`IClientTransportFactory`、`IServerTransportListener`、`ITransportConnection`、`IRpcSession`、`IRpcCodec`）
  - 结构化错误模型（`SharpLinkException` / `SharpLinkErrorCode`）
  - 生成代码注册表（`GeneratedProxyRegistry` / `GeneratedStubRegistry`）

- `SharpLink.Runtime`
  - `RpcSession`、`StreamManager`、`Request/Stream` 调度基础设施
  - `RpcCodecRegistry` 与内置编解码器
  - 传输实现（Socket、NamedPipe、AnonymousPipe 的 client factory / server listener / 独立 connection）
  - Protocol v2 帧编解码、发送泵、池化缓冲与并发容器

- `SharpLink.Sdk`
  - 只承载契约层标记和最小公共类型
  - 当前不承载 Builder；Builder 位于 `SharpLink.Client` 和 `SharpLink.Server`

- `SharpLink.Client`
  - `SharpClientBuilder`
  - 连接、心跳、请求跟踪、超时/取消管理
  - 客户端握手消息配置（`UseAuthenticator("token")`）
  - 原子连接状态机、自动重连、`ConnectAsync / StopAsync` 生命周期
  - 承载生成代理最终调用的 `IRpcChannel` 实现

- `SharpLink.Server`
  - `SharpLinkServerBuilder`
  - 连接接受、会话生命周期、服务分发
  - 服务端握手校验配置（`UseAuthenticator(message => bool/result)`）
  - 将当前 `sessionId + 认证上下文 + deadline + metadata` 挂入 `SharpLinkCallContext`
  - 通过 `SharpLinkAuthorization` 在服务方法内部执行 `scope / tenant / expiry` 校验
  - 调用 `IRpcStub` 执行真实服务方法

- `SharpLink.Hosting`
  - `AddSharpLinkServer()` / `AddSharpLinkClient()`
  - `HostedService` 托管封装与 `ISharpLinkClientAccessor`

- `SharpLink.Generator`
  - 扫描 `[RpcContract]` 接口与 `[RpcService]` 实现
  - 生成 `*_Proxy.g.cs`、`*_Stub.g.cs`
  - 输出编译期诊断（取消令牌、超时、泛型、契约继承等）

## Unary 调用链

1. 业务代码通过 `client.Get<TContract>()` 获取生成的 Proxy。
2. Proxy 将参数写入 payload，并调用 `SharpLinkClient` 的 `IRpcChannel` 实现。
3. Client 发送 `Request` 帧（包含 `contractId / methodId / requestId`）。
4. Server 根据 `interfaceHash` 找到 `IRpcStub` 与目标服务实例。
5. Stub 解码参数并调用真实服务方法。
6. 返回值编码为 `Response` 帧。
7. Client `RequestManager` 唤醒对应等待调用。

## 流式链路

- 客户端流：Client 按 `(requestId, streamId)` 发送 `StreamData / StreamComplete`
- 服务端流：Server 使用相同协议回推流元素，Client 侧 `StreamManager` 分发到对应 `Channel`
- 双向流：客户端流上传与服务端流下发同时存在
- 多流参数：同一请求内通过不同 `streamId` 区分

## 取消与超时

1. 调用侧 `CancellationToken` 触发，或请求超时调度器命中。
2. Client 发送 `ProtocolV2FrameType.Cancel`。
3. Server 定位目标请求 CTS 并取消执行。
4. 非 `oneway` 调用回传错误；流式调用完成对应流并结束等待。

## 错误传播

- 远端二进制错误码会映射为同码 `SharpLinkException`；错误消息受 64 KiB 上限约束
- 连接关闭、心跳超时、协议异常也会统一映射为带错误码的 `SharpLinkException`
- 用户取消保留 `OperationCanceledException`；deadline 到期映射为 `DeadlineExceeded`
- `StreamManager` 与 pending request 会共享同一错误对象，避免流式路径退化成普通字符串异常
- 握手拒绝现在也支持结构化传播；服务端可通过 `SharpLinkAuthenticationResult` 返回明确错误码与消息
- 服务方法执行期间可以通过 `SharpLinkCallContext.Current` 读取当前认证上下文，而不需要改动生成代理/Stub 签名
- 当前认证上下文已覆盖 `subject / tenantId / scopes / expiresAt / claims`
- 授权 helper 抛出的 `SharpLinkException` 会通过 `Response / StreamComplete(Error)` 保留原始错误码

完整线协议见 [`protocol-v2.md`](protocol-v2.md)。

## 序列化策略

- 默认内置基础类型与 blittable 容器编解码器
- JIT 场景通过 Client/Server Builder 的 `UseSerializer(MemoryPackCodec.Resolver)` 为当前实例启用 MemoryPack 兜底
- NativeAOT 场景通过 Builder 的 `UseCodec(MemoryPackCodec<T>.Instance)` 为当前实例显式注册复杂类型
- Codec Provider、Buffer Pool、状态容器配置都冻结在各自的 `SharpLinkRuntimeContext` 中，不允许 Builder 覆盖进程级可变配置

## 平台约束

- Unix/macOS 上 `NamedPipe` 由 .NET 映射到 Unix Domain Socket 路径
- 当前运行时会对过长的 pipe name 做确定性缩短，避免触发路径长度限制
- `AnonymousPipe` 适合本机协同进程，不适合跨主机场景
