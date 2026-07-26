# SharpLink 0.8.6 深度审核

English: [`en/audit-0.8.6.md`](en/audit-0.8.6.md)

以 0.8.5 commit `0152887` 为基线，本批完成五项 P2 以上实证修复：Stream transport writer/reader 异常不再跳过后续资源；RpcSession teardown 完整执行且并发 Dispose 共享结果；connection-scoped service cleanup 向监督任务报告全部失败；server-wide singleton/provider cleanup 保留全部 cause；Hosted Server 异步 run-loop 崩溃会记录并请求 Generic Host 停止。

五项均有预修复失败测试。Generator 83/83、Unit 369/369、Integration 228/228、Release build 与 package smoke 通过。迁移见 [`migration-0.8.6.md`](migration-0.8.6.md)，性能见 [`performance-0.8.6.md`](performance-0.8.6.md)。
