# SharpLink 0.8.29 深度审核

English: [`en/audit-0.8.29.md`](en/audit-0.8.29.md)

以 0.8.28 commit `a66eccc` 为基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | `PendingRequestTable.Dispose` 的扫描可先于并发插入结束，外部 50,000 次复现程序在第 1 次竞态留下未完成请求；两个 stream 注册入口在已销毁表上还可确定性插入。 | 所有入口统一做前置销毁检查；成功插入后再次检查并以 `ConnectionClosed` 收敛竞态。最终 50,000 次复核无 witness。 |
| P2 | 心跳用 `DateTime.UtcNow - LastActive`；未来墙钟值让无响应连接超过 3 秒仍保持 Ready，系统校时也可能误杀或永不超时。 | 收帧同时记录内部 `Stopwatch` 时间戳，Client/Server timeout 只使用单调 elapsed time；公开 UTC 属性保留作诊断。 |
| P2 | Pipe-backed 传输接受含 `/`、`\\` 或 NUL 的逻辑名，在不同平台变为路径或推迟到 BCL connect/accept 才失败。 | NamedPipe 与 SharedMemory 的统一名称规范化入口在构造期跨平台拒绝这三类字符。 |
| P2 | 抽象 Unix-domain 地址经 `ToString()` 快照后由 NUL 前缀变为 `@` 前缀，序列化长度也改变；listener 还会把显示名当文件路径清理。 | 通过 `Serialize/Create` 保留端点字节；抽象地址不参与文件存在检查与删除。 |
| P2 | `SharpLinkMultiClusterClient.State` 的 LINQ 枚举在 Ready/Degraded 每次读取分配 56 B。 | 直接枚举冻结字典并计数，语义不变且实测降为 0 B/read。 |

完整修复前 Unit 共 464 项，原有 459 项全部通过、新增五项恰好失败。补强后 Unit 464/464；并发测试另做 512 次同步起跑，所有已取得操作都完成且无残留槽。断言覆盖精确异常类型、终态、序列化字节、文件路径归属和线程分配，变异审查可击杀任一字符检查、前/后销毁检查、墙钟回退、字符串 UDS 快照或 LINQ 恢复。

另有一项不计版本的 P3 清理：Server 收帧循环已在统一入口更新 activity，Ping 分支原先重复写入 `LastActive`；单调时间引入后会同时重复采样两个时钟，现已删除该冗余操作。

最终非增量 Release 构建为 0 warning / 0 error，Generator 101/101、Unit 464/464、Integration 237/237、七包打包与全新缓存 package smoke 全部通过。性能证据见 [`performance-0.8.29.md`](performance-0.8.29.md)，迁移说明见 [`migration-0.8.29.md`](migration-0.8.29.md)。连续无新改进的完整审核轮次仍为 0/3。
