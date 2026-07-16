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
  - 每次连接通过 `ISharpLinkClientAuthenticator` 异步创建认证 payload
  - 原子连接状态机、自动重连、`ConnectAsync / StopAsync` 生命周期
  - 承载生成代理最终调用的 `IRpcChannel` 实现

- `SharpLink.Server`
  - `SharpLinkServerBuilder`
  - 连接接受、会话生命周期、服务分发
  - `ISharpLinkServerAuthenticator` 与显式 `RequireAuthentication()`
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

## Stream 字节流控

- Protocol v2 握手协商 `FlowControl` capability 以及 stream/connection receive window。
- 每个 `StreamData` 在进入 SendPump 前同时预留两级字节额度；额度不足的 producer 按 FIFO 异步等待，并受 cancellation、deadline 与 session 终态控制。
- 单个 item 只要未超过协商帧上限，可以在空窗口上临时借用一次，消费后必须完整归还。
- dispatcher 保存 decoded item 对应的 encoded byte count；消费者成功取走或丢弃 item 后累计 credit，达到半窗口时发送 `WindowUpdate`。
- 未知或已取消 stream 的迟到数据不会创建新 dispatcher；窗口溢出、重复 credit 和连续越窗均作为 `ProtocolViolation` 关闭连接。

## 客户端连接池

- 每个 Client endpoint 拥有一个冻结配置的有界池；默认 `MinConnections=1 / MaxConnections=1`。
- 单连接快路径直接返回唯一 session；多连接使用 power-of-two choices，从两个随机候选中选择 active request 较少者。
- 请求 ID 与创建时选中的 session 绑定，Unary、client/server stream 与 duplex 的响应、取消、超时和断连都通过同一绑定释放 active 计数。
- 只有当前候选已有在途请求时才合并触发一个扩容 worker，不能按每次调用创建连接。
- `GoAway` 将单条 session 标为 draining 并立即从选择快照移除；已有请求完成后释放该连接，池在后台恢复最小连接数。
- Client Stop 取消并等待 connect、reconnect、expand、heartbeat 与 read-loop worker，再释放所有 session 和 transport factory。

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

- 默认内置基础类型与 blittable 容器 Codec；RPC 可达的封闭 DTO/集合由 Source Generator 生成字段 ID Codec
- 生成 assembly manifest 只允许按 type/schema 幂等追加；冲突 schema 立即抛出
- 每个 Runtime Context 在 Build 时导入 manifest 快照，之后的注册不会改变已构建实例
- `[MemoryPackable]`、`[RpcExternalCodec]`、循环/多态图与第三方类型保留为显式插件边界
- 显式 Context Codec 优先于生成 Codec，MemoryPack resolver 只处理未生成且用户明确选择的类型
- Codec Provider、Buffer Pool、状态容器配置都冻结在各自的 `SharpLinkRuntimeContext` 中，不允许 Builder 覆盖进程级可变配置

## 平台约束

- Unix/macOS 上 `NamedPipe` 由 .NET 映射到 Unix Domain Socket 路径
- 当前运行时会对过长的 pipe name 做确定性缩短，避免触发路径长度限制
- `AnonymousPipe` 适合本机协同进程，不适合跨主机场景

## TCP TLS

- `SocketClientTransportFactory` 在 TCP connect 后执行 `SslStream.AuthenticateAsClientAsync`，成功后才进入 RPC handshake。
- Server accept loop 只负责快速接收 socket；每条 accepted connection 在独立、被追踪的生命周期任务中执行 TLS，慢客户端不会串行阻塞后续 accept。
- TLS handshake 默认 10 秒并可独立配置；timeout、server stop 与 caller cancellation 都会释放 socket、SslStream 和 Pipe。
- mTLS 直接使用 `SslServerAuthenticationOptions.ClientCertificateRequired` 与客户端证书集合；默认服务器证书验证不被框架放宽。
- 非 TCP transport 不创建 `SslStream`。协商后的 TLS protocol/cipher 可用于日志和后续 telemetry，但认证 payload、token 和证书敏感数据不写日志。

## 连接认证

- 默认无 provider 时为 Anonymous；要求身份的 Server 必须显式调用 `RequireAuthentication()`，否则 Build 不允许遗漏 provider。
- Client provider 每次连接尝试都会在 RPC handshake timeout 内重新执行，断线重连不会复用已过期 payload。
- Server provider 接收复制后的有界二进制 payload、connection ID 与 peer endpoint；异步执行期间不会持有 Pipe buffer。
- provider 返回的 `SharpLinkAuthenticationContext` 归属 session，并在每次调用创建 `SharpLinkCallContextSnapshot` 时传递。
- handshake 自动拒绝已过期 context；需要授权的业务调用可使用 `SharpLinkAuthorization` 或下一阶段 Server Interceptor 再次检查 token expiry。
- provider 未映射异常记录无 payload 的结构化日志，并向客户端返回不含内部细节的 `AuthenticationRejected`。
