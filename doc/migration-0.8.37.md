# SharpLink 0.8.37 迁移指南

English: [`en/migration-0.8.37.md`](en/migration-0.8.37.md)

0.8.37 不改变合法 Protocol v2 framing、route hash 或业务 payload，只把以前会生成非法 C# 或静默丢失派生状态的模型提前变成 SharpLink 编译期诊断。

## Service 与 DTO 可达性

`[RpcService]` 和会生成 native Codec 的 `[RpcSerializable]` 类型必须能从同程序集的 sibling generated namespace 访问。public、internal 与 protected internal 有效；private、protected、private protected、file-local，以及位于这些 containing type 下的声明会分别报告 `SHARPLINK018` 或 `SHARPLINK009`。把类型提升为 internal/public，或改用可达的显式 Adapter。

## Record 与 ref-like payload

Native generated record class 现在和其他 class 一样必须 sealed。若需要传输多态 record 图，请注册能保留运行时派生类型的 Codec Adapter；若只需要固定 schema，把 record 声明为 `sealed record`。

ref struct/span-like 值不能进入生成请求、响应或 Codec 泛型位置，现在报告 `SHARPLINK009` 并抑制损坏产物。改用可持久化 DTO、数组、`Memory<T>` 或 `ReadOnlyMemory<T>`。

## Contract 静态抽象成员

RPC contract 及其继承接口不能要求 Proxy 实现 static abstract operator/conversion；此类模型现在报告 `SHARPLINK054`。把泛型数学约束与 RPC contract 分离。普通默认/static 非抽象 helper 不受影响。

DTO 成员使用 C# keyword（例如 `public int @class { get; set; }`）无需迁移；0.8.37 会生成合法局部变量并保留正确成员访问。

admission/drain race probe 的变化仅影响测试门禁，不改变 Server 运行时行为。
