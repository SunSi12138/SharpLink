# 分配门禁与失败诊断

`eng/run-allocation-gate.sh` 使用 Release 构建运行真实 SharedMemory RPC 和 send-pump 场景。预算、样本数以及支持的 runtime major 以 `eng/perf/allocation-budgets.json` 为准。每个样本必须完成全部请求；每个场景同时检查中位数和全部样本的最大值减最小值，不能只根据中位数认定通过。

```bash
dotnet build test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release
./eng/run-allocation-gate.sh
```

报告头记录 runtime、操作系统、进程架构及 .NET 可见的逻辑处理器数量，便于核对环境差异。

原始 JSON 保留每个样本的操作完成数、累计分配以及 B/op。分配计数来自 `GC.GetTotalAllocatedBytes(precise: true)`，覆盖进程中的客户端、服务端、传输及其他 managed 线程。worker 和完成聚合任务在计数窗口前创建；样本间强制 GC 在窗口外执行。

每个样本的 `diagnosticsBefore` / `diagnosticsAfter` 额外记录 UTC、Stopwatch 时间戳、各代 GC 累计次数及线程池线程数、完成工作项数、待处理工作项数。它们是依次读取的进程统计，不能视为原子快照。待处理数量可以下降；线程池线程数不是进程的所有线程数。`stopwatchFrequency` 用于解释原始时间戳；`elapsedMilliseconds` 是两个诊断快照时间戳的跨度，包含计量边界开销，不是单次 RPC 服务耗时。

诊断采集放在原分配窗口外，快照存入窗口前已创建的 completion counter，避免扩大窗口内创建的 measurement async 状态机对象。这些字段不参与预算或稳定性判定；预热次数、操作数、样本数及阈值保持由原入口控制。

失败时保留原始报告、准确源码 tree 和 runtime 版本，再比较异常样本的耗时、GC 和调度变化。如仍不能解释，应单独收集 allocation trace 检查实际分配类型和调用栈。trace 会干扰测量；采样事件代表分配区间，不能把事件的整个 allocation amount 都归为该事件显示的单个对象类型。带 trace 的结果用于诊断，不能作为正式预算证书。

定位原因后在准确候选上重新验证。不能通过提高预算、删异常样本、只检查中位数或反复重跑到通过来消除一次尚未解释的失败。
