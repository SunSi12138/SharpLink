# SharpLink 0.8.35 迁移指南

English: [`en/migration-0.8.35.md`](en/migration-0.8.35.md)

0.8.35 不改变 Protocol v2 framing、route hash 或合法 payload，也不改变 `SharpLinkRuntimeContext.Options` 返回隔离副本的公共契约。

## 日志与运维

`LogEvents.Client.ResolverUpdateFailed = 6102` 是新增公共常量。由动态 Resolver worker 捕获并重试的解析、监听、snapshot 验证和 transport factory 构造失败现在是 Warning `6102`；真正未处理的后台故障仍是 Error `6002`。普通 transport/session 终止不再产生后台 Error，原有断线状态与重连行为不变。告警规则可监控 `6102` 的持续频率，同时继续把 `6002` 视为需处置的框架故障。

Chaos JSON 增加 `ServerErrors`，任意 Client/Server Error 均使门禁失败。显式请求的 JSON 无法写入时返回退出码 6；CI 应保留该非零退出，不应把缺失报告视为成功。

协议 teardown 与内部 profile 访问修复无需业务代码迁移。
