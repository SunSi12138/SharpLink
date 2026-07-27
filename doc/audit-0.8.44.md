# SharpLink 0.8.44 深度审核

English: [`en/audit-0.8.44.md`](en/audit-0.8.44.md)

以 0.8.43 commit `9789fbe` 为精确基线，本轮按独立工程根因确认一项 P1 与两项 P2。Server session、Server framework、Client background 与 static endpoint-cluster worker join 的多个表现来自同一个 `Task.WhenAll` 异常选择根因，只计一项。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | shutdown join 只检查 `await Task.WhenAll` 选中的异常；一个预期的连接关闭可遮蔽同组或嵌套任务中的内部失败。Client 还会在 shutdown token 取消后吞掉已完成 reconnect/expansion 的意外错误。 | 展开每个被跟踪任务的完整异常树，仅过滤明确的取消/transport 终态；初始 Connect 仍由原调用者观察，避免 Stop 二次报告。 |
| P1 | 同步 Server handler 失败后，如果 bounded send queue 拒绝 error/cancel response，后置的 call admission、request state、service lease 与 writer 释放不会执行，graceful stop 可永久等待已结束调用。 | 所有同步终态响应路径用 `try/finally` 绑定资源释放，原始 `ResourceExhausted` 仍向调用方传播。 |
| P2 | `StreamComplete`/`StreamError` 只有在终态帧成功入队后才关闭本地 send-flow state；queue rejection 会永久保留并发 stream slot。 | 终态帧入队与 `CompleteSendStream` 置于同一 `try/finally`，发送异常保持不变而 slot 必定释放。 |

确定性预修复见证分别保存于 `artifacts/0.8.44-prefx-server-mixed-session-cleanup.log`、`artifacts/0.8.44-prefx-client-reconnect-cleanup.log`、`artifacts/0.8.44-prefx-framework-join-mixed-failure.log`、`artifacts/0.8.44-prefx-static-cluster-worker-join.log`、`artifacts/0.8.44-prefx-server-error-enqueue-release.log` 与 `artifacts/0.8.44-prefx-stream-complete-slot.log`。修复后对应测试全部通过；初始 Connect 的 caller-owned failure 控制用例也通过。DNS 极长 jitter、shared-memory spill gate 与 multi-cluster cancellation callback 假设经实证否决，没有计入问题数。

三组完整 streaming 短样本全部零失败；其 c2s 信号不稳定，因此追加五组严格交错、每样本 2 秒预热 + 10 秒测量的 exact-0.8.43/candidate 对照。配对中位 QPS -0.05%、P50 -0.19%、P99 +0.27%、CPU/operation -0.38%，排除可测的热路径回退。详见 [`performance-0.8.44.md`](performance-0.8.44.md)。

最终非增量 Release 为 0 warning / 0 error，Generator 121/121、Unit 503/503、Integration 252/252。最终树的 120 秒共享内存 Chaos 完成 817,533 success、294,550 expected、0 unexpected、11 次重启，Client/Server Error 均为 0，最大恢复 216 ms；drain 与五项活跃指标全部归零。独立进程 SharedMemory NativeAOT、七包 pack 与 fresh-cache PackageSmoke 通过。本轮发现了新高价值问题，因此连续干净审核轮次保持 0/3；下一轮不以数量或版本号为目标。
