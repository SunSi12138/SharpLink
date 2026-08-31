# Generator 子系统架构

返回 [架构总览](architecture.md)。生产项目引用的规范边界见 [`project-reference-boundaries.md`](project-reference-boundaries.md)。

## 职责

`SharpLink.Generator` 是编译期子系统。它把契约和服务源码中可静态确定的信息转换为类型安全、可验证、可被 Runtime/Client/Server 使用的生成 Artifact。

主要职责包括：

- 扫描 `[RpcContract]` 契约和 `[RpcService]` 服务声明。
- 校验 RPC 方法形状、流式签名、取消/deadline 约束、泛型和继承等静态规则，并输出编译期诊断。
- 为 Contract 生成 Descriptor、Proxy、contract-based Stub、Codec 和 Manifest。
- 为 Service 生成 Descriptor、Activator，以及服务生命周期/依赖解析所需的静态 Artifact。
- 生成稳定的 wire/schema identity 与程序集级 Manifest/bootstrap 信息。
- 读取 Codec Adapter 的 Roslyn metadata 并生成静态绑定，不在 Generator 中加载第三方 serializer Runtime。

Generator 的目标是把“发现契约、决定调用形状、建立静态绑定”尽量前移到编译期，而不是把这些工作留给运行时反射。

## 依赖边界

规范生产依赖要求：

```text
SharpLink.Generator
  -> no SharpLink production ProjectReference

SharpLink.Sdk
  -. analyzer-only .-> SharpLink.Generator
```

因此：

- Generator 不能依赖 Runtime、Client、Server、Hosting、Serializer 或 Abstractions 的生产程序集引用。
- `SharpLink.Sdk -> SharpLink.Generator` 必须保持 analyzer-only：`OutputItemType=Analyzer` 且 `ReferenceOutputAssembly=false`。
- Generator 产生的运行时源码应面向稳定的 Abstractions/用户类型契约，而不是引用 Runtime、Client 或 Server 内部实现。
- Generator 不能通过“生成代码最终运行在 Runtime 上”反向建立对 Runtime 的编译期生产依赖。

这使 Generator 的演进与运行时实现解耦，也避免 Roslyn/Generator 依赖进入应用发布闭包。

## 所有权边界

Generator 拥有：

- 源码级契约/服务发现。
- 编译期合法性诊断。
- 生成 Artifact 的结构和确定性命名。
- Descriptor/Manifest 中可静态确定的契约元数据。
- 生成 Proxy/Stub/Codec/Activator 的静态调用路径。

Generator 不拥有：

- 网络连接、Session、frame、SendPump 或 stream dispatcher；这些属于 [Runtime](architecture-runtime.md)。
- endpoint 选择、连接池、重连、pending request；这些属于 [Client](architecture-client.md)。
- listener、服务 Registry、认证上下文和调用排空；这些属于 [Server](architecture-server.md)。
- 运行时可变的 Codec Provider/Manifest 注册快照；这些由 Runtime Context 管理。

## 编译期生命周期

Generator 没有应用运行时生命周期。其生命周期是：

1. Roslyn 提供当前 compilation、symbols 与 analyzer configuration。
2. Generator 建立契约/服务模型并执行静态验证。
3. 对有效模型生成源码；对无效模型产生诊断并阻止不安全 Artifact 成为“运行时才失败”的问题。
4. 编译器把生成源码与用户源码一起编译进目标程序集。
5. 应用运行后只使用这些生成 Artifact；不会创建或保留 Generator 实例。

因此运行时模块注册、替换或卸载不应重新调用 Source Generator。动态模块使用已经编译好的 Manifest/Artifact，并由 Runtime/Server 的实例级生命周期接管。

## 与 Runtime/Client/Server 的接口

Generator 与其他子系统通过生成代码和 Abstractions 契约协作：

- Proxy 是 Client 调用入口，但通过公共调用抽象进入 Client，而不是直接操作 Runtime Session。
- Stub 是 Server 的类型安全调用入口，但协议帧的读取/写入仍由 Runtime 机制负责。
- Codec/Manifest 为 Runtime 提供静态元数据和工厂入口，Runtime 决定实例级注册、缓存和所有权。
- Activator 为 Server 提供类型安全的服务构造路径，Server 仍负责 Scope、服务生命周期和排空。

这条边界允许 Client、Server、Runtime 的内部实现独立重构，只要稳定 Abstractions 与 Generated ABI 契约保持兼容。

## 性能与 NativeAOT 约束

Generator 是 SharpLink NativeAOT/低开销设计的关键前置层：

- 优先生成闭合泛型和直接调用代码，避免运行时 `MakeGenericType`、`Activator`、程序集扫描或按调用反射。
- 生成结果应确定且可缓存；同一输入不应依赖运行时环境产生不同 wire/schema identity。
- build-time Generator 依赖不能泄漏到发布程序集或 NativeAOT dependency closure。
- 静态 Contract/Service 场景应有完整生成路径；动态加载能力不能迫使静态快路径依赖动态代码生成。
- 新增 Generator 功能时，应先判断信息是否可在编译期确定；如果可以，不应为了实现方便把发现逻辑推迟到 Runtime。

Codec/Manifest 的运行时所有权与动态模块约束见 [`contracts-and-codecs.md`](contracts-and-codecs.md) 和 [`dynamic-modules-and-multicluster.md`](dynamic-modules-and-multicluster.md)。

## 变更归属判断

通常属于 Generator 的变更：契约语义分析、新诊断、生成源码结构、Generated ABI、静态 Descriptor/Manifest/Codec/Activator 生成。

通常不属于 Generator 的变更：连接/协议状态机、endpoint 策略、服务运行时生命周期、认证、重试、接入控制或 telemetry policy。此类需求应进入 Runtime、Client、Server 或专题设计，而不是通过 Generator 持有运行时策略。
