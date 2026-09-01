# Runtime 子系统架构

返回 [架构总览](architecture.md)。生产项目引用的规范边界见 [`project-reference-boundaries.md`](project-reference-boundaries.md)。

## 职责

`SharpLink.Runtime` 是 Client 和 Server 共享的协议与传输机制层。它负责“如何把已经决定好的 RPC 调用可靠地编码、调度和传输”，但不负责业务侧 endpoint、重试、认证授权或服务生命周期策略。

主要职责包括：

- Protocol v2 帧编解码、握手与 capability 的机制实现。
- 物理 `RpcSession`、读写循环、SendPump 与连接终态。
- Request/Stream dispatcher、stream terminal 和 flow-control 机制。
- Socket、NamedPipe、AnonymousPipe、SharedMemory 等 transport factory/listener/connection 实现。
- Runtime Context 所属的 Codec Provider、Manifest 注册快照、Buffer Pool 与相关有界缓存。
- 共享的池化缓冲、并发容器和协议错误映射基础设施。

Runtime 应保持“机制无业务策略”：Client 和 Server 可以复用同一套 Session/transport/frame 能力，而不把彼此的策略耦合进 Runtime。

## 依赖边界

规范生产依赖要求：

```text
SharpLink.Runtime
  -> SharpLink.Abstractions
```

Runtime 不得引用 `SharpLink.Client`、`SharpLink.Server` 或 `SharpLink.Hosting`。这意味着：

- Runtime 不能通过具体 Client/Server 类型表达状态或回调。
- Client/Server 所需的共享接口必须放在 Abstractions，或由 Runtime 提供不含上层策略的内部机制。
- Runtime 不应为了某个 Client/Server 功能增加反向引用；需要上层策略时应通过稳定抽象或参数注入。
- Serializer 扩展不能通过 Runtime 形成新的生产引用边；Codec Adapter 通过 Abstractions/Manifest 契约接入。

## 所有权边界

Runtime 拥有：

- 单条物理连接和 Session 的协议状态。
- frame 读取、编码、发送顺序和 stream/flow-control bookkeeping。
- transport connection 的创建、读取、写入和释放机制。
- Runtime Context 内部的 Codec/Manifest/Buffer 配置与缓存所有权。
- 协议级终止、资源释放和迟到 frame 的机制处理。

Runtime 不拥有：

- endpoint discovery、负载均衡、Retry、Circuit Breaker 或 connection-pool policy；这些属于 [Client](architecture-client.md)。
- 动态 Contract/Proxy 或 Service module generation 的对外发布、替换、注销与 drain policy；这些由 Client/Server 实例拥有，Runtime Context 只承接相应 registration 的运行时状态。
- 服务 Registry、服务实例生命周期、认证/授权、异常映射 policy 或 admission；这些属于 [Server](architecture-server.md)。
- 契约发现、源码诊断或生成 Artifact；这些属于 [Generator](architecture-generator.md)。
- Generic Host 的应用启动/停止 policy；Hosting 只包装 Client/Server 生命周期。

## Runtime Context 生命周期

Runtime Context 是运行时共享机制的实例级所有权边界：

1. Builder/上层组件提供 Codec、Buffer、Manifest 等配置。
2. Build 阶段验证并冻结配置，避免调用进行中读取进程级可变选项。
3. 静态 Manifest 快照被导入 Context；动态模块由 Client/Server 通过各自的实例 API 发布、替换或注销 generation，Runtime Context 接收对应 registration identity 并维护其 Codec/Manifest runtime state，而不拥有上层 module publication policy。
4. Codec/cache 绑定对应的 registration identity；替换/卸载不能让旧 generation 清理误删新 generation 状态。
5. Context 释放时，Runtime 自己创建并拥有的资源必须随之释放；调用方显式提供且保留所有权的对象不得被越权释放。

动态注册、替换与 collectible ALC 的细节见 [`dynamic-modules-and-multicluster.md`](dynamic-modules-and-multicluster.md)。

## Session 生命周期

物理 Session 是 Runtime 的核心状态机边界。高层顺序为：

1. 上层选择/接受 transport connection。
2. Client/Server 使用 Runtime 提供的握手 frame、capability negotiation 和 session-phase 机制，分别驱动各自的握手与认证编排，并建立协商后的 Session 状态。
3. read loop、SendPump、dispatcher 和 flow-control 机制在 Session 生命周期内协同工作。
4. Client/Server 把调用或服务分发动作挂接到 Session，但不直接接管 frame bookkeeping。
5. 任一协议违规、transport 失败或显式停止进入单一终止路径，取消等待者并释放 transport、buffer 和 dispatcher 状态。

Client/Server 可以给终止附加自己的业务语义，但不能绕过 Runtime 的单一资源清理边界。

## Streaming 与 flow-control 边界

Runtime 负责所有 stream 的 wire/mechanism 语义：

- `(requestId, streamId)` 路由和 stream terminal。
- `StreamData` / `StreamComplete` 等帧的机制处理。
- stream/connection 两级字节额度、`WindowUpdate` 和协议违规检测。
- late/unknown stream 数据的机制处置。

Client 决定调用侧何时取消、deadline 到期或 consumer abandoned；Server 决定服务 invocation 何时完成/失败。Runtime 负责把这些上层决定安全地反映到协议与资源状态。

## 性能与 NativeAOT 约束

Runtime 是稳态热路径，架构上要求：

- 配置在 Build/Context 边界冻结，热路径避免读取进程级可变配置。
- buffer、request/stream state 和 sequence segment 等高频资源优先池化且有界。
- 没有启用的可选能力不应创建对应后台 worker、delegate graph、反射缓存或每调用对象。
- 协议/Codec 路径依赖 Generated Artifact 和闭合泛型入口，不以运行时程序集扫描、动态类型构造或动态代码生成为基础。
- NativeAOT 静态路径只依赖可静态分析的 Runtime/Abstractions 代码；动态模块能力必须作为显式、可隔离的运行模式存在。
- transport/platform 特性必须在能力边界失败，不允许把平台探测散落到上层 Client/Server policy。

具体传输和平台约束见 [`transports.md`](transports.md)，调优和硬限制见 [`limits-and-tuning.md`](limits-and-tuning.md)。

## 变更归属判断

通常属于 Runtime 的变更：wire frame、Session、SendPump、stream dispatcher、flow-control、transport、Runtime Context、基础 Codec/Manifest runtime 机制。

如果需求涉及 endpoint policy、重试、认证、服务实例、DI scope、接入控制或业务异常映射，优先在 Client/Server 处理。Runtime 只应提供足以实现这些策略的通用机制，不应成为“所有共享代码”的落点。
