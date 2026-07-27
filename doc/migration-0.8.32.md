# SharpLink 0.8.32 迁移指南

English: [`en/migration-0.8.32.md`](en/migration-0.8.32.md)

0.8.32 不改变公共 API、Protocol v2 framing、合法 payload 或生成代理/桩。它收紧已有配置冻结与错误边界，并优化启用 admission 后的常见同步成功路径。

## 自定义压缩 Provider

`ISharpLinkCompressionProvider.WireProfile` 在 Runtime Context `Build()` 时校验并冻结。该 Context 后续的客户端广告、服务端选择、查找和 session 诊断都使用这一快照；构建后修改 provider 属性不会更改 wire identity。provider 实例本身仍由框架调用，仍必须线程安全并在其使用期内保持压缩算法行为兼容。

## 认证、timeout 与 Unix socket

- `SharpLinkAuthenticationResult.Reject` 现在拒绝未定义 `SharpLinkErrorCode`。如果 provider 通过 public constructor 返回未定义 rejection，Server 会安全地发送 `AuthenticationRejected`。
- 任意正的默认 request timeout 仍合法；超出 `DateTimeOffset` 表示范围时 deadline 饱和到最大值，而不是在发送前抛异常。
- UDS listener 只有能证明路径仍是自己绑定的 socket node 时才删除；identity 捕获失败时可能保留一个 stale socket path，调用方可在启动失败后显式清理已知归属路径。

Admission 的规则、排队、公平性和 lease 生命周期不变；单 concurrency limiter 的即时成功仅减少内部临时分配。
