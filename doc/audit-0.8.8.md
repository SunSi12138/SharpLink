# SharpLink 0.8.8 深度审核

English: [`en/audit-0.8.8.md`](en/audit-0.8.8.md)

以 0.8.7 commit `30da5f7` 为基线，本批五项 P2 以上问题均有预修复失败实证：匿名管道清理异常会泄漏输入句柄；共享内存控制通道清理异常会泄漏映射；单个动态模块、多动态模块和 Server 全局服务清理分别会丢失后续失败。预修复 Unit 379 项中恰有五项新增回归失败、既有 374 项通过；修复后 Unit 379/379，并完成完整门禁。

迁移见 [`migration-0.8.8.md`](migration-0.8.8.md)，性能见 [`performance-0.8.8.md`](performance-0.8.8.md)。
