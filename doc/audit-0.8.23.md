# SharpLink 0.8.23 深度审核

English: [`en/audit-0.8.23.md`](en/audit-0.8.23.md)

以 0.8.22 commit `3a4338d` 为基线，本批确认五项 P2 改进：Boolean blit collection 接受非规范元素；Rune/decimal collection 绕过 scalar 校验；DateOnly/DateTime/TimeOnly collection 可构造非法时间值；DateTimeOffset collection 接受非法 ticks/offset 并传播每元素 6-byte padding；截断的 shared-memory server response 从 Client Connect 泄漏原始 `EndOfStreamException`。

完整前置 Unit 运行共 449 项，原有 445 项全部通过、新增四项恰好失败；完整前置 Integration 运行共 237 项，原有 236 项全部通过、新增确定性 truncated-peer 探针失败。修复覆盖 array、List、Memory、ReadOnlyMemory、ImmutableArray 五个 public Codec shape；DateTimeOffset 以专用 writer 规范化 padding，避免让普通 blit 写入承担额外分支。握手仅在 server response 尚未完成的非取消 I/O 失败时映射为 `Unavailable`。

修复后非增量 Release 构建为 0 warning / 0 error，Generator 84/84、Unit 449/449、Integration 237/237、七包打包与全新缓存 package smoke 全部通过。两个会令普通 `int[]` 写入从约 10.3 ns 增至 12.6/10.8 ns 的共享 helper 方案均被否决；最终 ordinary path 回到约 10.1 ns。详见 [`performance-0.8.23.md`](performance-0.8.23.md) 与 [`migration-0.8.23.md`](migration-0.8.23.md)。
