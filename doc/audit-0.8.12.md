# SharpLink 0.8.12 深度审核

English: [`en/audit-0.8.12.md`](en/audit-0.8.12.md)

以 0.8.11 commit `a10081f` 为基线，本批五项 P2 以上问题均有预修复故障注入：直接 Client 的 profile 绑定与后续构造失败不会释放 Client-owned transport；动态 endpoint Client 的验证失败不会释放 resolver；Server 服务验证会被 Runtime Context 清理异常覆盖；logger 构造失败则完全绕过 Server 的旧清理边界。定向预修复运行恰有五项失败，0.8.11 已提交基线为 Unit 394/394；修复后 Unit 399/399 并完成完整门禁。

最终实现只在原有 Client 外层异常冷路径聚合 transport/resolver 与 Runtime Context 回滚，避免给正常分支增加异常边界。Server 构建改为成功返回时才转交注册表、内部 Provider、Admission Controller 与 Runtime Context；失败时逐一释放并保留完整有序错误集。

迁移见 [`migration-0.8.12.md`](migration-0.8.12.md)，性能见 [`performance-0.8.12.md`](performance-0.8.12.md)。
