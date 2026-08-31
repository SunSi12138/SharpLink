# Client 子系统架构

返回 [架构总览](architecture.md)。生产项目引用的规范边界见 [`project-reference-boundaries.md`](project-reference-boundaries.md)。

## 职责

`SharpLink.Client` 拥有 RPC 调用发起侧的实例配置、连接策略和调用生命周期。它把 Generator 生成的 Proxy 调用转换为对 Runtime Session 的受控使用，并把网络/协议结果还原为调用侧语义。

主要职责包括：

- `SharpClientBuilder` 与客户端实例配置冻结。
- endpoint 配置、静态/动态 topology snapshot 和 endpoint selector。
- connection pool、连接建立、heartbeat、reconnect、draining 和扩缩容策略。
- request ID、pending request、request-to-session 绑定和在途计数。
- 调用侧 cancellation、monotonic deadline、consumer-abandoned 等终态仲裁。
- 每次连接的认证 payload 创建。
- Generator Proxy 最终调用的 `IRpcChannel` 实现。
- Client interceptor、调用侧 telemetry 和 resilience policy 的实例级编排。

## 依赖边界

规范生产依赖要求：

```text
SharpLink.Client
  -> SharpLink.Runtime
  -> SharpLink.Abstractions

SharpLink.Client -X-> SharpLink.Server
SharpLink.Client -X-> SharpLink.Hosting
```

因此 Client 可以直接使用 Runtime 的 Session/transport 机制和 Abstractions 的公共契约，但不能依赖 Server 或 Hosting 的具体实现。

Client/Server 的共享需求不能通过互相引用解决。若共享内容是稳定公共契约，应进入 Abstractions；若只是 frame/session/transport 机制，应进入 Runtime；若是单侧策略，则留在对应子系统。

## 所有权边界

Client 拥有：

- endpoint/topology 与连接池策略。
- connect/reconnect/expand/heartbeat 等客户端后台 worker。
- pending call、request-to-session 绑定、调用侧 cancellation/deadline 终态。
- 何时允许新调用、何时选择连接、何时把连接从候选集合移除。
- Client interceptor/resilience 的 logical-call 与 attempt 语义。

Client 不拥有：

- frame codec、SendPump、stream dispatcher、transport connection 的底层读写；这些属于 [Runtime](architecture-runtime.md)。
- 服务 Registry、服务实例、认证判定或异常映射；这些属于 [Server](architecture-server.md)。
- Proxy 源码形状与契约静态分析；这些属于 [Generator](architecture-generator.md)。

## 实例生命周期

客户端实例高层生命周期为：

1. Builder 收集 endpoint、transport、auth、resilience、interceptor 和 Runtime Context 相关配置。
2. Build 验证配置并冻结客户端实例；调用路径不再依赖可变 Builder。
3. `ConnectAsync` 建立初始连接/Session，并发布可用于调用的连接快照。
4. 调用期间 Client 选择 endpoint/connection、建立 pending call，并把请求交给 Runtime。
5. 断连、GoAway 或 topology 变化由 Client policy 更新候选集合，并按配置进行重连/替换/排空。
6. `StopAsync` 是终止边界：停止接收新工作，取消并等待 Client 自己拥有的后台 worker 和 pending lifecycle，再释放所拥有的 Session/transport factory。

Runtime Session 的关闭会通知 Client 完成对应 pending work，但 Session 本身不拥有 Client 的重连或 endpoint 策略。

## 调用状态与 Session 绑定

一次调用的重要所有权关系是：

```text
logical call
  -> endpoint/attempt policy
    -> selected physical session
      -> request id / pending call
        -> Runtime frame exchange
```

请求一旦选择 Session，响应、取消、deadline、断连和 in-flight 计数释放必须围绕同一绑定完成，避免在重连或扩容后把旧请求错误地归属到新 Session。

Streaming 沿用同一原则：Client 拥有调用侧 producer/consumer 生命周期，Runtime 拥有 stream frame 和 flow-control 机制。

## Endpoint 与 resilience 边界

Client 负责 endpoint discovery、负载均衡、Retry、Circuit Breaker、admission-to-attempt 等策略，因为这些决定“是否以及在哪里发起一次尝试”。Runtime 只负责已经选定的 Session 上如何传输。

静态单 endpoint 是基础快路径；只有显式配置多 endpoint/resolver 时才应创建 topology worker 和 selector 状态。Retry 只在明确满足资格的 logical call 上产生新的 attempt，不得由 Runtime 在 transport failure 后自行重放业务请求。

完整策略见 [`resilience.md`](resilience.md)。

## 性能与 NativeAOT 约束

Client 位于用户调用热路径，架构上要求：

- Generator Proxy 通过静态 `IRpcChannel` 路径进入 Client，不依赖运行时代理生成或契约扫描。
- 单 endpoint、单 connection、无 interceptor/telemetry/resilience 的默认路径保持最短，不因未启用能力创建额外 worker 或策略对象图。
- endpoint snapshot 和 connection candidate 集合以不可变/原子发布方式供读路径使用，避免调用热路径争用 topology writer lock。
- pending call、request operation 等高频状态必须有界；池化复用不能改变单一完成/清理语义。
- deadline/heartbeat 以 monotonic elapsed time 做正确性判断，墙钟只用于诊断。
- NativeAOT 场景依赖生成 Proxy 与静态 Runtime 入口；Client 不能引入运行时动态代理或反射式服务发现作为基础路径。

调优和默认限制见 [`limits-and-tuning.md`](limits-and-tuning.md)。

## 变更归属判断

通常属于 Client 的变更：连接池、endpoint topology、reconnect、pending request、调用侧取消/deadline、Client auth payload、Retry/Breaker、Client interceptor/telemetry policy。

如果变更只涉及 frame/transport/flow-control，应进入 Runtime；如果涉及服务实例、认证判定、服务调用上下文或 admission，应进入 Server。
