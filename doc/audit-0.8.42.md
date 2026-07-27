# SharpLink 0.8.42 深度审核

English: [`en/audit-0.8.42.md`](en/audit-0.8.42.md)

以 0.8.41 commit `d0e0df4` 为精确基线，本批确认一项 P1 与四项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P1 | Throughput profile 每 1 ms 取消 single-reader Channel waiter；流式生产与取消竞争会让 async operation 双重完成并以退出码 134 终止进程。 | deadline 只与独立 delay 竞争；底层 Channel wait 在超时后保留给下一轮消费，始终至多一个 outstanding waiter。 |
| P2 | 非 nullable `Memory<T>`/`ReadOnlyMemory<T>` 接受数组/列表专用的 `-1` null marker 并静默变为空。 | 在 shape 边界拒绝 marker，返回结构化 `DataLoss`；nullable array/list 与 default immutable 对照不变。 |
| P2 | fixed nullable primitive 接受 null marker 后任意非零 ignored value body，允许同一值有多种 wire 表示。 | 复用固定宽度读取分支验证 1/2/4/8/16-byte body 全零；present 热路径与 segmented input 都受控。 |
| P2 | cancel/health 与 handshake public writers 复用 peer validator，把无效本地参数误报为 `ProtocolViolation`。 | writer 在写入前抛 argument exception；reader 保持 peer `ProtocolViolation` 分类。 |
| P2 | generated runtime DTO Codec `SchemaId` 忽略 reference member nullability。 | 只为 nullable member 追加 schema identity 标记，区分 required/nullable，同时保持既有 non-nullable identity。 |

修复前 Generator 的 120 个既有测试全部通过，只有新的独立编译 DTO schema identity 见证失败；Unit 的 490 个既有测试全部通过，三个新增/扩展见证分别证明 Memory null coercion、非规范 nullable body 与 writer 错误域。独立精确基线负载中，0.8.41 的 `operation=all` 两次均退出 134，s2c 为 3/5 崩溃、c2s 为 5/5 崩溃。修复后累计 16/16 Throughput 进程、64/64 unary/c2s/s2c/duplex 阶段零失败。

最终非增量 Release 为 0 warning / 0 error，Generator 121/121、Unit 493/493、Integration 250/250。十进程 exact 0.8.41/candidate TCP unary 中位 QPS 为 166,576 → 165,315（-0.76%），P50 均为 48 µs、P99 稳定，allocation 约 172.08 → 171.98 B/call。nullable `int?` present decode 为 5.155 → 5.090 ns/op，canonical null 验证为 5.444 → 5.937 ns/op（绝对增加 0.493 ns），两者均零分配；首版 21.5 ns 包装器方案已因回退被拒绝。

120 秒共享内存 Chaos 完成 814,834 success、319,230 expected、0 unexpected、23 次重启，Client/Server Error 均为 0，最大恢复 218 ms；drain 与五项活跃指标全部归零。NativeAOT TCP、七包 pack 与 fresh-cache TCP/shared-memory functional smoke 通过。本轮仍发现新改进，连续无新改进轮次保持 0/3。
