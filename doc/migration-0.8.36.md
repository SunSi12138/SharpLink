# SharpLink 0.8.36 迁移指南

English: [`en/migration-0.8.36.md`](en/migration-0.8.36.md)

0.8.36 不改变合法 Protocol v2 framing、route hash 或业务 payload。它收紧握手 response 的既有一致性规则，并包含一项源代码级 API 删除。

## 移除 per-call 压缩开关

删除 `SharpLinkCallOptions.EnableCompression`。该成员在 0.8.35 及更早版本没有成功路径：设为 `true` 总是抛 `Unimplemented`，设为 `false` 也不会覆盖已协商的自动压缩。删除调用初始化器即可：

```csharp
var options = new SharpLinkCallOptions
{
    Timeout = TimeSpan.FromSeconds(2),
    WaitForReady = true
};
```

要启用压缩，请继续在 Client 与 Server 的 `UseRuntime` 中注册兼容 Provider。要禁用一端发送压缩，请勿在该端注册 Provider；要调整选择范围，请配置 `MinimumPayloadBytes`、`MinimumSavingsBytes` 与 `MinimumSavingsRatio`。

## 配置与停止语义

显式设置 `FlowControl.MaxSendQueueBytes = 8 * 1024 * 1024` 现在会覆盖 LowLatency/Throughput profile 默认，和其他显式非默认值一致。未赋值时仍分别使用 1/8/32 MiB 的 profile 默认。

正常 Stop 会等待已无活动调用的 connection-scoped service 异步 Dispose。超过 graceful timeout 的不合作业务调用仍不会无界阻塞 Stop，其服务继续在调用最终排空后后台释放。

自定义 Protocol v2 工具若构造 `ProtocolV2HandshakeResponse`，必须让 Compression capability 与非空 `CompressionProfile` 同时出现或同时缺失；以前可编码但对 Client 无效的矛盾值现在立即失败。
