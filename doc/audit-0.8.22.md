# SharpLink 0.8.22 深度审核

English: [`en/audit-0.8.22.md`](en/audit-0.8.22.md)

以 0.8.21 commit `481989c` 为基线，本批确认五项 P2 改进：generated DTO Boolean 接受非规范位模式；Rune 与 decimal 绕过各自语义校验；DateOnly、DateTime、TimeOnly 可由畸形 payload 构造非法值；DateTimeOffset 同时接受非法 ticks/offset，并把 16-byte native layout 中的 6-byte padding 原样发送。

完整前置 Integration 运行共 236 项，原有 231 项全部通过、新增五项恰好失败；完整前置 Generator 运行共 84 项，原有 83 项全部通过、新增 emitted-source 探针失败。最终实现保留既有 field ID、fixed wire type 与 payload size：Boolean 使用规范 0/1 helper，其余语义类型使用可内联的专用 fixed reader，DateTimeOffset writer 额外清零 padding。nullable sibling 由生成源码计数断言覆盖。

修复后非增量 Release 构建为 0 warning / 0 error，Generator 84/84、Unit 445/445、Integration 236/236、七包打包与全新缓存 package smoke 全部通过。首版 length-delimited Codec 方案因 66/109 ns 的明显回退被否决，最终 fixed-wire 方案保持分配不变且总成本约 1–2 ns；详见 [`performance-0.8.22.md`](performance-0.8.22.md) 与 [`migration-0.8.22.md`](migration-0.8.22.md)。
