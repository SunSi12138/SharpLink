# SharpLink 0.8.10 深度审核

English: [`en/audit-0.8.10.md`](en/audit-0.8.10.md)

以 0.8.9 commit `afe9f3a` 为基线，本批五项 P2 以上问题均有预修复失败实证：固定 endpoint 构建、profile 绑定、单 Manifest 准备、多 Manifest Context 构建和 Client 外层 Context 回滚会丢失主失败或清理失败。预修复 Unit 389 项中恰有五项新增回归失败；修复后 Unit 389/389 并完成完整门禁。

迁移见 [`migration-0.8.10.md`](migration-0.8.10.md)，性能见 [`performance-0.8.10.md`](performance-0.8.10.md)。
