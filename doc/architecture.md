# SharpLink 架构说明

## 分层视图

```text
Application
  -> Client / Server
    -> Runtime
      -> Abstractions
Service
  -> Sdk
Generator (编译期) --------------> 生成 Proxy/Stub 到业务项目
Serializer.MemoryPack ----------> 提供 ISerializer 实现
Hosting ------------------------> Host 集成层
```

## 各模块职责

- `SharpLink.Abstractions`
  - 协议常量（`PacketType/PacketFlags`）
  - 核心接口（`IRpcChannel`, `IRpcStub`, `ITransport`, `ISerializer` 等）
  - `RpcSession`、`StreamManager` 等基础模型

- `SharpLink.Runtime`
  - 传输实现与底层收发辅助
  - Packet 编解码与发送队列

- `SharpLink.Sdk`
  - `SharpClientBuilder` / `SharplinkServerBuilder`
  - 常用传输层扩展（TCP/UDS/NamedPipe/AnonymousPipe）

- `SharpLink.Client`
  - 连接握手、心跳、请求分发
  - 同步/异步调用、流式调用、oneway 调用
  - 请求跟踪（`RequestManager`）与取消包发送

- `SharpLink.Server`
  - 会话管理、心跳检查、请求路由
  - 调用 `IRpcStub` 执行服务方法
  - 处理流式数据与协议级取消

- `SharpLink.Generator`
  - 扫描 `IService` 接口和 `[RpcService]` 实现
  - 生成 `*_Proxy.g.cs` 与 `*_Stub.g.cs`
  - 编译期方法签名约束与诊断

- `SharpLink.Hosting`
  - 提供 `AddSharplinkServer()` / `AddSharplinkClient()`
  - 通过 `IHostedService` 托管生命周期

## 调用链路（Unary）

1. 业务调用 `client.Get<T>().Method(...)`
2. 生成的 Proxy 将参数编码为 payload
3. Client 发送 `RpcCall` 包（含 `interfaceHash/methodHash/requestId`）
4. Server 根据 hash 定位 Stub + Service 实例
5. 生成的 Stub 解码参数，调用真实服务方法
6. 返回值编码为 `RpcResponse`
7. Client `RequestManager` 唤醒对应等待任务

## 流式链路

- 客户端流：Client 通过 `StreamChunk/StreamComplete/StreamError` 上送流元素
- 服务端流：Server 发送同样的流包给 Client，Client 侧 `StreamManager` 分发到对应 `Channel`
- 双向流：上述两套机制同时存在
- 多流参数：通过 `(requestId, streamId)` 组合键区分

## 取消链路

1. 调用侧 `CancellationToken` 触发
2. Client 发送 `PacketType.Cancel`（携带 `requestId`）
3. Server 定位请求 CTS 并取消执行
4. 非 oneway 请求由 Server 回错误响应，客户端结束等待

## 设计取舍

- 生成多入口调用方法，避免在 `IRpcChannel` 里用单一大入口做高频分支
- 参数编解码优先考虑 blittable 快路径与变长对象兼容
- 在流式模型中统一 `StreamManager` 作为请求内多流复用枢纽
