# SharpLink 0.8.38 迁移指南

English: [`en/migration-0.8.38.md`](en/migration-0.8.38.md)

0.8.38 不改变合法 Protocol v2 framing、route hash 或 payload wire layout，只把以前会生成非法 C# 的构造/指针模型提前变成 SharpLink 诊断，并纠正 interceptor 的结构化取消状态。

## Service 构造器

`[RpcService]` 的选定 public 构造器必须能由 generated `IServiceProvider` activator 调用。`ref`/`out`/`ref readonly`、ref-like、pointer 和 function-pointer 依赖现在报告 `SHARPLINK019`。改用普通可注册依赖；只读 `in` 参数仍受支持，但普通按值参数更符合 DI 约定。

## DTO 构造计划

Native generated DTO 必须满足 C# required-member 规则，包括 `[RpcIgnore]` 成员和 required field。若构造器自行完成这些成员，请按 C# 约定标记 `[SetsRequiredMembers]`；否则让成员由 public setter/init 或 public field initializer 赋值。`ref`/`out`/`ref readonly` DTO 构造器不再被 generated Codec 选择，可增加匹配成员的普通值构造器；`in` 仍有效。

## Pointer payload 与取消状态

pointer/function-pointer 不能作为 generated RPC payload，现在报告 `SHARPLINK009` 并抑制损坏 Proxy/Stub。请改用整数句柄、稳定 DTO 或显式安全封装；这类 CLR 值不能通过普通 `IRpcCodec<T>` 泛型表达。

Client/Server interceptor 观察到 `SharpLinkException` 且 `Code=Cancelled` 时，Context 的 `Status` 现在为 `Cancelled`。依赖旧有矛盾 `Failed` 状态的日志或策略应改为按结构化取消处理。
