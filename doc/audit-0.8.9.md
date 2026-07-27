# SharpLink 0.8.9 深度审核

English: [`en/audit-0.8.9.md`](en/audit-0.8.9.md)

以 0.8.8 commit `c90525b` 为基线，本批五项 P2 以上问题均有预修复失败实证：共享内存 control 清理失败后跳过 reader 收敛；单 Client 与多集群 Hosted Stop 并发调用提前返回；异步 listener 并发 Dispose 提前返回；匿名管道 listener 在首个连接清理失败后跳过后续连接。预修复 Unit 384 项中恰有五项新增回归失败；修复后 Unit 384/384 并完成完整门禁。

迁移见 [`migration-0.8.9.md`](migration-0.8.9.md)，性能见 [`performance-0.8.9.md`](performance-0.8.9.md)。
