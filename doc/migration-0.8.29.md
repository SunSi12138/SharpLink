# SharpLink 0.8.29 迁移指南

English: [`en/migration-0.8.29.md`](en/migration-0.8.29.md)

0.8.29 不改变 Protocol v2 framing、payload 或生成代码，默认配置无需迁移。

- NamedPipe 与 SharedMemory 的 `name` 是逻辑标识，不再接受 NUL、`/` 或 `\\`。若现有名称刻意包含目录，请改用不含路径字符的稳定标识；SharpLink 负责平台实际路径。
- Linux 抽象 Unix-domain endpoint（构造字符串首字符为 NUL）现在保持抽象 namespace，不再被错误地转换成以 `@` 开头的文件 socket，也不会触发文件清理。
- `IRpcSession.LastActive` 仍可读写并记录最近收帧的 UTC 时间，供兼容与诊断使用；写入该属性不再改变框架 heartbeat timeout。依赖手工写 `LastActive` 延长连接存活的代码应改为真实协议流量。
- 在 `PendingRequestTable` 已销毁后开始的 stream 注册现在与 unary 注册一致，抛出 `ObjectDisposedException`；与销毁并发、但已成功插入的调用以 `ConnectionClosed` 完成。
- `SharpLinkMultiClusterClient.State` 的 Ready/Degraded 结果不变，只移除了读取分配。
