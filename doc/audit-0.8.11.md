# SharpLink 0.8.11 深度审核

English: [`en/audit-0.8.11.md`](en/audit-0.8.11.md)

以 0.8.10 commit `e84c851` 为基线，本批五项 P2 以上问题均有预修复失败实证：Client/Server 的运行时注册与替换在结构化拒绝和 generated Adapter Scope 回滚同时失败时会丢失主失败；Server profile 绑定失败发生在清理边界之外，会泄漏刚构建的 Runtime Context。预修复 Unit 394 项中恰有五项新增回归失败，既有 389 项全部通过；修复后 Unit 394/394 并完成完整门禁。

Server 候选服务回滚同时改为逐一尝试清理，避免首个第三方释放异常跳过后续所有者。正常结构化拒绝仍返回原有结果；只有回滚也失败时才抛出以事务失败为首因的 `AggregateException`。

迁移见 [`migration-0.8.11.md`](migration-0.8.11.md)，性能见 [`performance-0.8.11.md`](performance-0.8.11.md)。
