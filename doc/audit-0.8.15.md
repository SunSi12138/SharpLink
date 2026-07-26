# SharpLink 0.8.15 深度审核

English: [`en/audit-0.8.15.md`](en/audit-0.8.15.md)

以 0.8.14 commit `b32f846` 为基线，本批确认五项 P2 以上问题：Unix-domain listener 会删除任意既有路径；Socket Client factory 保留调用方可变 `IPEndPoint`；内置 endpoint delegate 保留可变 socket/TLS/shared-memory options；Direct Client builder 会把同一 transport/resolver 转交给多个 Client；Server builder 既会重复转交 listener，也不会在后期 Build 失败时释放它。

预修复完整 Unit 探针共 417 项，其中原有/未受影响的 410 项通过，7 个聚焦 case 失败。短 `/tmp` 实证确认旧 listener 会覆盖普通文件；真实 loopback 实证确认修改构造参数会把 factory 的连接目标改成端口 0；其余断言分别观察三类冻结配置、Client transport/resolver 的唯一所有者，以及 Server 成功/失败两条 listener 转移路径。最终实现拒绝替换任何既有 Unix 路径、复制已知 endpoint、在 delegate 创建时冻结配置，并从 builder 清除已经转移或回滚释放的单所有者资源。修复后 Unit 417/417；非增量 Release 构建（0 warning/0 error）、Generator 83/83、Integration 228/228、七包打包与全新缓存 package smoke 全部通过。

迁移见 [`migration-0.8.15.md`](migration-0.8.15.md)，性能见 [`performance-0.8.15.md`](performance-0.8.15.md)。
