# SharpLink 0.8.21 深度审核

English: [`en/audit-0.8.21.md`](en/audit-0.8.21.md)

以 0.8.20 commit `726992c` 为基线，本批确认五项 P2 以上改进：shared-memory mapping path 会替换畸形 UTF-8 后再做安全校验；generated null collection 跳过 root trailing-byte 校验；generated DTO string 与请求 metadata 会把孤立 UTF-16 surrogate 静默改写为 U+FFFD；动态 per-call service 的 scope factory 抛错会泄漏 module call lease。

完整前置 Unit 运行共 445 项，原有 441 项全部通过、新增四项恰好失败；完整 Integration 运行共 231 项，原有 230 项全部通过、新增 generated collection 探针失败。修复统一在所有权转移或写入前拒绝无效文本、补齐 null collection 完整消费，并把 scope creation 纳入 module lease 清理区。另以全仓零引用证据删除两个未使用 internal helper；压缩输出越界候选因 writer lease 已有硬上限而淘汰。

修复后非增量 Release 构建为 0 warning / 0 error，Generator 83/83、Unit 445/445、Integration 231/231、七包打包与全新缓存 package smoke 全部通过。性能取舍见 [`performance-0.8.21.md`](performance-0.8.21.md)，迁移见 [`migration-0.8.21.md`](migration-0.8.21.md)。
