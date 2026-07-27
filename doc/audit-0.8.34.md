# SharpLink 0.8.34 深度审核

English: [`en/audit-0.8.34.md`](en/audit-0.8.34.md)

以 0.8.33 commit `35c8cd2` 为基线，本批五项版本推进 P2 与审核中追加的两项 P2 均有独立实证。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | 120 秒共享内存 Chaos 在 419,817 次成功调用、152,238 次注入故障和 11 次重启后捕获两个 `SharedMemoryPipeReader.ReadAsync` NRE。Completion 只等待已返回 buffer，不等待尚未发布结果的 read operation。 | Completion 的释放握手同时覆盖 `_readOperationPending` 与 outstanding `ReadResult`；无活动时仍同步完成，不引入锁。 |
| P2 | 同一报告含两个客户端 Error 却仍以 `Passed`/0 退出，且 generation `Clear()` 会抹去旧 Error。 | Chaos 保留有界的分代与全程 Error 队列及单调计数；任意 Error 使门禁失败，并增加可执行注入自测。 |
| P2 | 相同 CLR 签名的继承声明可在 `[Oneway]` 上冲突，生成结果取决于基接口顺序。 | 扩展 `SHARPLINK057`，在产物生成前拒绝 call-shape 冲突。 |
| P2 | 同一折叠还忽略 Timeout、Idempotent 与 NonCancellable，导致 deadline/retry/cancellation 策略顺序依赖。 | 每个方法一次提取执行策略并按签名代表项比较；明确 derived redeclaration 可作为 canonical policy。 |
| P2 | 序列化参数名、顶层及嵌套 nullability 会进入 request schema，却未参与继承冲突判断。 | 以 `IncludeNullability` 比较完整 payload 参数类型并比较参数名；CancellationToken/CallOptions 控制参数不被误判，真实冲突抑制 Proxy/Stub。 |
| P2 | 修正 Error oracle 后，正常 transport teardown 的终止态 `AdvanceTo` 被 Client/Server 循环升级成后台 Error。 | 仅在 session 已断开或 token 已取消时接受该终止竞态；非终止的非法 cursor 仍失败。 |
| P2 | 服务器重启打断池扩容握手时，框架会捕获并重试，却记录 `BackgroundLoopUnhandledException` Error。静态/动态集群同样分类错误。 | 固定、静态和动态扩容/重连统一使用新增 Warning 事件 `6101`；真正未处理后台异常仍为 Error `6002`。 |

Generator 预修复 107 项中原有 104 项全部通过，仅三个新冲突探针失败；Unit 预修复 478 项中原有 477 项全部通过，仅 pending-read 探针失败。追加的连接分类断言在旧实现稳定失败。断言与伪变异复核覆盖：精确诊断数量、冲突产物抑制、derived redeclaration 正例、参数名与嵌套 nullability、pending/outstanding 两种 reader owner、Chaos 真实进程退出/报告、以及 Warning/Error 分类。一个既有 pool collectability 测试也移除了 async/JIT 临时变量生命周期假设，不改变生产代码。

最终 120 秒共享内存 Chaos 完成 863,299 次成功调用、310,349 次预期注入故障与 11 次重启，0 次意外失败、0 条客户端 Error，排空成功且五项活跃指标全部为 0；独立进程 NativeAOT 输出 `AOT_SMOKE_CLIENT_PASS`。最终非增量 Release 构建为 0 warning / 0 error，Generator 108/108、Unit 478/478、Integration 238/238、七包与全新缓存 smoke 全部通过。性能证据见 [`performance-0.8.34.md`](performance-0.8.34.md)，行为说明见 [`migration-0.8.34.md`](migration-0.8.34.md)。连续无新改进轮次为 0/3。
