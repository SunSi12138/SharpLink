# SharpLink 0.8.34 迁移指南

English: [`en/migration-0.8.34.md`](en/migration-0.8.34.md)

0.8.34 不改变 Protocol v2 framing、route hash 或合法 payload。它收紧有歧义的继承 RPC 契约，并调整可恢复连接失败的日志分类。

## 继承 RPC 契约

相同 CLR 方法签名的继承声明现在必须在返回类型、Oneway call shape、Timeout/Idempotent/NonCancellable 策略、序列化参数名与完整 nullability schema 上一致，否则报告一个 `SHARPLINK057` 且不生成该契约的 Proxy/Stub。CancellationToken/CallOptions 等控制参数的名称不属于 request schema。若多个基接口有意提供不同元数据，可在派生契约中显式 `new` redeclare 一次以选择 canonical 语义；真正不兼容的返回类型仍必须重命名或拆分。

## 日志与运维门禁

`LogEvents.Client.ConnectionAttemptFailed = 6101` 是新增公共常量。框架已捕获、会按现有策略恢复的固定端点/集群扩容与重连失败从 Error `6002` 改为 Warning `6101`；真正未处理的后台任务故障仍使用 Error `6002`。若告警规则依赖旧事件 ID，请改为监控 `6101` 的持续频率，并继续把 `6002` 视为需要处置的框架错误。

共享内存 reader owner、终止态 `AdvanceTo` 与 Chaos oracle 修复不需要业务代码迁移。
