# SharpLink 0.8.36 深度审核

English: [`en/audit-0.8.36.md`](en/audit-0.8.36.md)

以 0.8.35 commit `8f55419` 为精确基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | Server 在第二次确认 Running 后才递增全局活动计数；Stop 可在两者之间观察 0 并完成 drain，而请求仍返回 Acquired。 | 先发布全局计数，再做最终状态确认；Draining 竞态完整回滚全局与连接计数。 |
| P2 | 连接关闭把 connection-service cleanup 放入未监督 fire-and-forget task；无活动调用时 Server Stop 仍可早于异步 Dispose 返回。 | 对已无活动调用的 retired connection 把 cleanup 纳入 framework task；不合作调用仍按既有 bounded deferred cleanup 收敛。 |
| P2 | 性能档案用“值是否等于 8 MiB”猜测 queue 是否显式配置，导致显式 8 MiB 被 Throughput 改成 32 MiB。 | `SharpLinkFlowControlOptions` 独立保留赋值意图，Clone/冻结复制该状态；profile 只填充未赋值默认。 |
| P2 | `SharpLinkCallOptions.EnableCompression=true` 在唯一消费点无条件抛出 Unimplemented，即使压缩已注册并协商；false 也不能禁止自动压缩。 | 删除无可用语义的公共成员；连接级协商与三个收益阈值继续自动控制所有业务 payload。 |
| P2 | 公共握手 response writer/reader 接受 Compression capability 与 selected profile 缺一项的自相矛盾值，只靠 Client 更晚的调用方检查补救。 | writer 在写入前抛参数错误，reader 在返回前报告 ProtocolViolation；四种缺失方向均有断言。 |

预修复 Unit 共 483 项，原有 479 项全部通过，仅四项新增探针失败；其中 192,000 次有界调度扫描在 0.47 秒内捕获到真实 late admission。Integration 共 240 项，原有 239 项全部通过，仅阻塞 connection-service Dispose 的 Stop join 探针失败。修复后 Unit 483/483、Integration 240/240；断言/伪变异复核覆盖最终状态读取位置、双计数回滚、监督/延迟清理分界、显式默认值、移除的 API，以及 codec 双向一致性。

第一版准入修复新增第三次状态读取，精确微基准为 5.3769 -> 5.7210 ns（+6.4%），因此被拒绝。最终实现通过调整计数发布顺序恢复两次读取的热路径形状；三进程中位数的中位数为 5.1399 -> 5.1706 ns（+0.60%），两者均为 0 分配。

最终 120 秒共享内存 Chaos 完成 846,971 次成功调用、331,401 次预期注入故障与 23 次重启，0 次意外失败、Client/Server Error 均为 0；排空成功且五项活跃指标全部为 0。独立 NativeAOT 输出 `AOT_SMOKE_PASS transport=tcp`；最终非增量 Release 构建为 0 warning / 0 error，Generator 108/108、Unit 483/483、Integration 240/240，七个 0.8.36 包预提交打包成功。完整性能证据见 [`performance-0.8.36.md`](performance-0.8.36.md)，迁移说明见 [`migration-0.8.36.md`](migration-0.8.36.md)。本轮仍发现新改进，连续无新改进轮次保持 0/3。
