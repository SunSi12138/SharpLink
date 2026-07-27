# SharpLink 0.8.35 深度审核

English: [`en/audit-0.8.35.md`](en/audit-0.8.35.md)

以 0.8.34 commit `044598c` 为基线，本批五项版本推进 P2 与审核中追加的两项 P2 均有运行时实证。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | Resolver 的 Resolve/Watch、非法 snapshot 与 factory 构造失败由 worker 捕获并重试，却使用“未处理后台异常”Error `6002`。 | 新增 Warning `6102`，只用于由 Resolver 状态机拥有恢复的失败；意外清理失败继续使用 Error。 |
| P2 | Chaos 只接入 Client logger，Server 使用空 logger；注入 Server Error 后仍以 `Passed`/0 退出。 | Client/Server 分别保留有界全程 Error 证据，任一侧 Error 都使门禁失败，并提供 Server 注入自测。 |
| P2 | 显式 `--json-output` 指向不可写位置时，报告失败被吞掉且进程仍返回 0。 | 报告失败使用单调计数，避免递归重写不可写目标，并以专用退出码 6 失败。 |
| P2 | Server 在仍持有 `ReadResult` 时等待 session disposal；会等待 reader completion 的 transport 因循环尚未 `AdvanceTo` 而自锁。Client 非法入站帧有对称风险。 | 终止分支立即返回，由循环 finally 先释放 buffer，再由外层生命周期关闭连接。 |
| P2 | 内部只读 `PerformanceProfile` 却经过公共 `Options` 深拷贝，在每次 Client/Server Build 与每条物理连接创建 send pump 时分配完整配置副本。 | 在既有 friend assembly 边界使用 frozen context 的内部只读 profile；公共防御性快照语义不变。 |
| P2 | TCP 滚动重启产生的普通 reset 已被 Client 转换为断线/重连，却先记录未处理后台 Error。 | Client 对 IO/socket/disposed/structured connection-close 走预期连接终止路径，协议与内部异常仍为 Error。 |
| P2 | Server 滚动停止与业务错误响应竞争时，`SendRpcErrorAsync` 可得到结构化 `ConnectionClosed`，随后被记为未处理后台 Error。 | Server 对普通 IO/socket/disposed/structured connection-close 做同样分类，不改变真正故障的 Error。 |

预修复 Unit 479 项中原有 478 项全部通过，仅新 Resolver 日志探针失败；Integration 239 项中原有 238 项全部通过，仅 completion-joining reader 探针失败。真实 Chaos 进程分别证明 Server Error 假通过、报告不可写假通过、Client TCP reset Error，以及启用双端 oracle 后暴露的 Server 停止竞态。断言与伪变异复核覆盖 Warning 的 severity/event/exception、禁止 Error、reader 完成与 `AdvanceTo` 顺序、双端注入的退出/报告，以及不可写报告退出码。

最终 120 秒共享内存 Chaos 完成 818,793 次成功调用、302,335 次预期注入故障与 11 次重启，0 次意外失败、Client/Server Error 均为 0；排空成功且五项活跃指标全部为 0。双端 Error 注入分别按预期退出 2，不可写报告探针退出 6；独立 NativeAOT 输出 `AOT_SMOKE_PASS transport=tcp`。最终非增量 Release 构建为 0 warning / 0 error，Generator 108/108、Unit 479/479、Integration 239/239。性能证据见 [`performance-0.8.35.md`](performance-0.8.35.md)，行为说明见 [`migration-0.8.35.md`](migration-0.8.35.md)。本轮仍发现新改进，连续无新改进轮次保持 0/3。
