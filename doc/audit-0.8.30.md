# SharpLink 0.8.30 深度审核

English: [`en/audit-0.8.30.md`](en/audit-0.8.30.md)

以 0.8.29 commit `88039d5` 为基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | Hosted Server 的 Run observer 只在成功路径检查 `_stopRequested`；Stop 中 listener cleanup 令 Run fault 时仍会记录 Critical 并调用 `StopApplication`。 | fault 路径同时检查显式 Stop 与 Host stopping，保留 Stop 原始异常但不误杀 Host。 |
| P2 | 对尚未 Start 的 hosted service 调用 Stop 后，后续 Start 仍能发布新 Server；缓存的完成 Stop task 再也不会清理它。 | Start 与 Stop 通过同一 lifecycle gate 线性化，Stop 成为终态；重复 Start 也同步拒绝。 |
| P2 | Generator 用 `ReturnType.Contains("ValueTask")` 判断外层任务；合法的 `Task<ValueTaskPayload>` 被当成 `ValueTask<T>`，Proxy/Stub 生成错误 await/return 代码。 | `RpcMethodModel.ReturnsValueTask` 只匹配规范化返回类型的外层 `global::System.Threading.Tasks.ValueTask` 前缀。 |
| P2 | 具体 pipe transport 已拒绝路径字符，但公共 `SharpLinkNamedPipeAddress` / `SharpLinkSharedMemoryAddress` 仍接受，resolver 路径继续延迟失败。 | Abstractions 提供统一内部 logical-name 校验，地址值与 Runtime transport 共享。 |
| P2 | `SharpLinkServerHealthCheck` 每次本地探测用 `Task.FromResult` 分配 96 B；100,000 次实测 9,600,000 B。 | Ready/Draining/Unhealthy 三个固定结果各缓存一个完成 Task，探测后分配为 0 B。 |

完整预修复证据由两个全套测试组成：Generator 102 项中原有 101 全过、仅新任务形状测试失败；Unit 468 项中原有 464 全过、仅另外四项失败。补强测试验证 Host stopping token、Stop 异常身份、Stop 后 readiness、Proxy/Stub 两侧 Task 代码、三个非法字符和精确线程分配。变异审查可击杀任一 Stop 检查、lifecycle gate、外层前缀、公共地址校验或缓存结果分支。

综合性能模式扫描还逐项审查了 0 async-void、4 个 `.Result`、1 个同步 Wait、8 个 Generator Substring、60 个 stackalloc、142 个 LINQ signal、19/269 个未 sealed/sealed class 等命中；没有把构建、注册、清理冷路径或刻意可扩展类型机械改写。最终非增量 Release 构建为 0 warning / 0 error，Generator 102/102、Unit 468/468、Integration 237/237、七包与全新缓存 smoke 全部通过。详见 [`performance-0.8.30.md`](performance-0.8.30.md) 与 [`migration-0.8.30.md`](migration-0.8.30.md)。连续无新改进轮次仍为 0/3。
