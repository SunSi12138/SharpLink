# SharpLink 0.8.16 深度审核

English: [`en/audit-0.8.16.md`](en/audit-0.8.16.md)

以 0.8.15 commit `8b6eeaa` 为基线，本批确认五项 P2 以上问题：Client 的超长 deadline 会超出原生 `Timer` 范围并在 pending slot 已占用后抛出；Runtime Context 释放后仍保留 writer pool 数组；Server Stop 吞掉即时清理失败；Hosted Server 把临时启动令牌错误当成长生命周期令牌；pending table 接受可导致每连接多 GB 数组的容量。

预修复完整 Unit 共 422 项，原有 417 项全部通过，五个聚焦探针恰好全部失败。原生计时器实证给出 4,294,967,294 ms 上限；其余探针直接观察 retained buffer、成功返回但 listener 清理失败的 Stop、启动令牌取消后的服务终止，以及 2,097,152-slot 配置被接受。最终实现以安全间隔重挂 deadline timer、让 Context 关闭并排空 pool、传播即时 stop failures、把 Hosted Run 改用独立 lifetime CTS，并在 public/internal 两层限制 pending capacity。修复后 Unit 422/422；非增量 Release 构建（0 warning/0 error）、Generator 83/83、Integration 228/228、七包打包与全新缓存 package smoke 全部通过。

迁移见 [`migration-0.8.16.md`](migration-0.8.16.md)，性能见 [`performance-0.8.16.md`](performance-0.8.16.md)。
