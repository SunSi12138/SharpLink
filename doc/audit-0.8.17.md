# SharpLink 0.8.17 深度审核

English: [`en/audit-0.8.17.md`](en/audit-0.8.17.md)

以 0.8.16 commit `0e4e1a7` 为基线，本批确认五项 P2 以上问题：并发 multi-cluster assembly unregister 会重复执行同一 child operation，并可能用路由恢复冲突替换原始失败；TLS 快照共享或漏掉可变 chain policy，Server 还漏掉受支持平台上的 RSA padding 设置；握手接受 required 不属于 supported 的矛盾能力集合及未知 negotiated response 位；分区准入池保留调用方可变配置；state store 和 writer pool 接受无硬上限的聚合资源配置。

预修复完整 Unit 共 427 项，原有 422 项全部通过，五个聚焦探针恰好全部失败。探针分别直接观察两个 child unregister 调用与被替换的异常、共享/丢失的 TLS policy、被接受的矛盾能力集合、构建后仍可改变 live partition limit 的源对象，以及被接受的越界聚合容量。最终实现让并发注销共享单一 coordinator task，完整深复制 TLS 与 admission 配置，在 payload codec 边界验证协商完整性，并为 stripe、initial map entries 与 retained writer memory 增加聚合硬上限。

修复后非增量 Release 构建为 0 warning / 0 error，Generator 83/83、Unit 427/427、Integration 228/228、七包打包与全新缓存 package smoke 全部通过。Integration 门禁还确认未知 request capability 必须继续交由协商层返回 `Unimplemented`，因此只拒绝矛盾 request 集合和未知 negotiated response，不封死未来 request 扩展位。

迁移见 [`migration-0.8.17.md`](migration-0.8.17.md)，性能见 [`performance-0.8.17.md`](performance-0.8.17.md)。
