# SharpLink 0.8.28 深度审核

English: [`en/audit-0.8.28.md`](en/audit-0.8.28.md)

以 0.8.27 commit `656271b` 为基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | `KeepAliveTime`/`KeepAliveInterval` 接受 `TimeSpan.MaxValue`，随后在 socket 建立路径以 checked `int` 秒转换溢出。 | 配置冻结时按 2,147,483,647 秒原生上界校验两个字段。 |
| P2 | TokenBucket、FixedWindow、SlidingWindow 接受超过便携 timer 范围的周期，把失败推迟到 BCL limiter 构造。 | 三类周期统一在规则配置阶段限制为 2,147,483,647 ms。 |
| P2 | NamedPipe client/server 接受未定义 option bit 与 transmission mode，client 还接受 server-only `FirstPipeInstance`，factory/listener 可以构造但无法可靠 connect/accept。 | 构造时按 client/server 分别校验支持的 flag 与 mode，保留合法组合。 |
| P2 | SlidingWindow 允许 `SegmentsPerWindow > Window.Ticks`，分段周期下取整为零。 | 要求每段至少一个 `TimeSpan` tick，并保留恰好一 tick 的边界。 |
| P2 | `ProtocolV2PayloadCodec.WriteError` 可写出未定义但落在 `ushort` 内的错误码，而对应 reader 必然拒绝。 | 在写任何字节前复用完整错误码集合校验。 |

完整修复前 Unit 共 459 项，原有 454 项全部通过、新增五项恰好失败。补强测试同时覆盖恰好最大值可接受、上界多一 tick 被拒绝、server 接受全部已定义命名管道 flag 而 client 拒绝 `FirstPipeInstance`、恰好一 tick 的 SlidingWindow segment 可接受，以及非法错误码不会留下部分 payload。

审核中另外否定了三个候选：在受支持的 .NET 10/arm64 上，DNS 与 retry 的最大时长 jitter 转换会饱和而非回绕；接近 `int.MaxValue` 的超额 flow-control credit 已被归类为 `ProtocolViolation`。pending-table Dispose/Rent 竞态仍缺少确定性 witness，因此不计入本版，也未为凑数修改。

最终非增量 Release 构建为 0 warning / 0 error，Generator 101/101、Unit 459/459、Integration 237/237、七包打包与全新缓存 package smoke 全部通过。运行时交替 A/B 保持原分配并通过 5% 门禁；配置冷路径的纳秒级新增校验成本与错误帧写出数据见 [`performance-0.8.28.md`](performance-0.8.28.md)。迁移说明见 [`migration-0.8.28.md`](migration-0.8.28.md)。
