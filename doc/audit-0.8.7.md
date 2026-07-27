# SharpLink 0.8.7 深度审核

English: [`en/audit-0.8.7.md`](en/audit-0.8.7.md)

以 0.8.6 commit `3d5da89` 为基线，本批五项 P2 以上问题均有预修复失败实证：ClientConnection 并发 Dispose 提前返回；Runtime Context 丢失多个 Adapter scope 失败；Hosted Server 并发 Stop 不共享完成；connection close 丢失第二条终止异常；取消回调抛错会阻断 pending call/stream 终止。修复后 Unit 374/374，并完成完整门禁。

迁移见 [`migration-0.8.7.md`](migration-0.8.7.md)，性能见 [`performance-0.8.7.md`](performance-0.8.7.md)。
