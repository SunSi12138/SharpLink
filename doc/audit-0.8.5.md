# SharpLink 0.8.5 深度审核

English: [`en/audit-0.8.5.md`](en/audit-0.8.5.md)

本批以 0.8.4 commit `a7f8a24` 为基线，审核 Hosting 发布、Client 初始连接池、Server service lifetime 与 RPC 终止清理。五项互相独立的 P2 以上问题均先由失败测试实证，再修复并完成完整验证。

| 等级 | 问题与实证 | 修复与验证 |
| --- | --- | --- |
| P1 | `SetClient` 与 `Stop`/`Fail` 分离更新状态；受控竞态可在 Stop 清空后重新发布 client，`GetClientAsync` 随后返回已停止实例。 | 发布与终止写入共用 gate；读取先后复核 terminal state，100,000 次竞态只能抛出停止异常。 |
| P2 | Call/Connection service factory 与 scope rollback 同时失败时，cleanup 异常覆盖 activation 根因。 | 两条 lifetime 路径都聚合 activation 与 scope cleanup；分支测试验证两条 cause。 |
| P2 | Service 与 scope 同时释放失败时，Call lease 和 Connection instance 主动吞掉 scope 异常。 | 所有清理层均继续执行；单一异常保留原栈，双异常按发生顺序聚合。 |
| P1 | 固定 Client 建立最小连接池时，后续 connect 失败后的首个 disposal 异常会中断回滚、覆盖 connect 根因，并跳过 `Faulted` 转换。 | 回滚所有已建立连接，聚合 connect/cleanup 异常，并在抛出前稳定发布 `Faulted`。 |
| P2 | RPC handler 已失败时，`InvokeServiceWithLeaseAsync` 静默丢弃 request-stream completion 或 lease cleanup 失败。 | handler、stream completion 与 lease cleanup 逐层执行并合并 terminal exception，供 mapper 与诊断观察。 |

完整 Generator 83/83、Unit 364/364、Integration 228/228、Release 构建与 0.8.5 package restore/run smoke 通过。迁移见 [`migration-0.8.5.md`](migration-0.8.5.md)，性能门见 [`performance-0.8.5.md`](performance-0.8.5.md)。
