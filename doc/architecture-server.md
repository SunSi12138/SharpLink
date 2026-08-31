# Server 子系统架构

返回 [架构总览](architecture.md)。生产项目引用的规范边界见 [`project-reference-boundaries.md`](project-reference-boundaries.md)。

## 职责

`SharpLink.Server` 拥有 RPC 调用接收侧的实例配置、服务注册和 invocation 生命周期。它把 Runtime 提供的 Session/frame 机制与 Generator 生成的 Stub/Activator 组合成可托管、可认证、可排空的服务端执行边界。

主要职责包括：

- `SharpLinkServerBuilder` 与服务端实例配置冻结。
- listener/accepted connection 的服务端编排和 Session 生命周期接入。
- 服务 Registry、Contract owner、服务替换和服务实例生命周期。
- `ISharpLinkServerAuthenticator`、`RequireAuthentication()` 与认证上下文建立。
- `SharpLinkCallContext`、metadata、peer、deadline 和认证上下文的调用侧传播。
- Server interceptor、异常映射、授权 helper 与服务端 telemetry policy。
- Unary/OneWay/Streaming invocation 的开始、完成、取消、排空和资源释放。
- admission control、health/draining 等“是否接收/执行新调用”的服务端策略。

## 依赖边界

规范生产依赖要求：

```text
SharpLink.Server
  -> SharpLink.Runtime
  -> SharpLink.Abstractions

SharpLink.Server -X-> SharpLink.Client
SharpLink.Server -X-> SharpLink.Hosting
```

Server 可以使用 Runtime 的 transport/session/frame 机制和 Abstractions 的公共契约，但不能依赖 Client 或 Hosting 的具体实现。

服务端需要与 Client 共享的协议机制应由 Runtime/Abstractions 承载，而不是建立 Server↔Client 引用。Generic Host 集成位于 Hosting，Server 本身保持可独立构建和运行。

## 所有权边界

Server 拥有：

- listener 接入策略和 accepted connection 的服务端生命周期编排。
- 服务 Registry、服务实例/Scope、替换与排空。
- 认证结果、调用上下文、服务端 interceptor 和异常映射 policy。
- invocation cancellation/deadline 的服务端终态和迟到响应抑制策略。
- admission、health、draining 等服务可用性状态。

Server 不拥有：

- frame codec、SendPump、stream dispatcher、flow-control 和 transport 原语；这些属于 [Runtime](architecture-runtime.md)。
- endpoint selection、connection pool、reconnect 或 Retry；这些属于 [Client](architecture-client.md)。
- Stub/Activator 的源码生成和契约静态验证；这些属于 [Generator](architecture-generator.md)。

## 实例与监听生命周期

服务端实例高层生命周期为：

1. Builder 收集 transport/listener、service registration、auth、interceptor、admission 和 Runtime Context 相关配置。
2. Build 验证并冻结配置，形成稳定的服务 Registry 与实例级 policy。
3. Run/Start 打开 listener，接受物理连接，并为每条连接建立 Runtime Session 与服务端 connection state。
4. 每次调用根据生成 Descriptor/Stub 与 Registry 建立 invocation state 和 `SharpLinkCallContext`。
5. Streaming 的服务实例/Scope 和调用状态保持到整条流真正终止，而不是只保持到方法返回一个 enumerable/stream handle。
6. Stop/Draining 停止接收新工作并等待已接收调用按规则完成；实例终止后释放 listener、Session、服务 Scope 和 Server 自己拥有的资源。

Generic Host 的启动/停止包装见 [`hosting-and-services.md`](hosting-and-services.md)，但 Server 的核心状态机不应依赖 Hosting。

## 服务 Registry 与生成 Artifact

Generator 提供 Stub、Descriptor、Activator 和 Manifest；Server 决定这些 Artifact 如何被一个具体 Server 实例采用：

- Build 阶段合并可见 registration 并验证一个 Contract 的服务所有权。
- Stub 提供类型安全的调用入口；Server 负责选择目标服务实例、Scope 和 invocation lifetime。
- Activator 提供静态构造路径；Server/DI 负责依赖 Scope 的创建和释放。
- 动态 replace/register/unregister 必须以实例级 generation/ownership 语义发布，旧 generation 在相关调用排空前不能被提前释放。

动态模块的完整语义见 [`dynamic-modules-and-multicluster.md`](dynamic-modules-and-multicluster.md)。

## 认证、调用上下文与异常边界

Server 是网络输入进入业务代码前的策略边界：

- 连接认证在服务调用前建立 session-owned authentication context。
- 每次调用创建稳定的调用上下文，包含 contract/method、request、peer、metadata、deadline 和认证信息。
- 业务授权由显式 policy/helper 执行，Runtime 不理解 tenant/scope 等业务概念。
- `IRpcExceptionMapper` 在服务端边界把业务异常转换为可公开的结构化错误；Runtime 只负责编码和传输已经映射的结果。
- cancellation/deadline 必须先建立稳定终止原因，再触发业务取消，避免回调观察到竞争中的状态。

安全细节见 [`security.md`](security.md)。

## Admission 与排空边界

Admission control 决定调用是否进入服务执行，因此属于 Server policy，而不是 Runtime transport policy。permit、queue、partition 等状态必须与 invocation 生命周期一致释放；未启用 admission 时，基础服务调用路径不应承担其对象分配和异步等待成本。

Server draining 同样是服务端可用性语义：它决定是否接受新调用以及何时可以释放服务 generation。Runtime Session 的 transport close 只是底层机制，不能替代 Server 的服务排空规则。

完整 admission 设计见 [`admission-control.md`](admission-control.md)。

## 性能与 NativeAOT 约束

Server 既是网络入口也是服务调用热路径，架构上要求：

- 使用 Generator 生成 Stub/Activator，避免运行时扫描服务方法、动态生成代理或反射式构造作为默认路径。
- 空 interceptor/admission/telemetry pipeline 应保持直接调用快路径，不为关闭的能力创建 delegate chain、Task 或每调用对象。
- 调用表、排队、stream spool 和连接级状态必须有界，并在取消/断连/排空的所有终态释放。
- service registration 和 policy 在 Build/generation 发布边界形成稳定快照，调用热路径不读取可变 Builder。
- NativeAOT 静态部署依赖生成 Artifact 与可静态分析的 DI/调用路径；动态模块能力不能破坏静态服务路径的可裁剪性。

服务端限制与调优见 [`limits-and-tuning.md`](limits-and-tuning.md)。

## 变更归属判断

通常属于 Server 的变更：listener 编排、服务 Registry、服务生命周期/DI Scope、认证与调用上下文、异常映射、Server interceptor、admission、health/draining。

如果变更只涉及 frame/session/transport/flow-control，应进入 Runtime；如果涉及 endpoint、重连、pending request 或 Retry，应进入 Client。
