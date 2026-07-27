# SharpLink 0.8.36 性能验证

English: [`en/performance-0.8.36.md`](en/performance-0.8.36.md)

在 Apple M4 / .NET SDK 10.0.102 上，使用独立 exact `8f55419` worktree 与候选运行相同的 Server `TryAcquireCall`/`ReleaseCall` 循环。每个进程先预热 1,000,000 次，再运行 21 组、每组 5,000,000 次；缓存私有 delegate 的固定成本两侧相同。

| 构建 | 三个进程的 21-sample 中位数 | 中位数的中位数 | 分配 |
|---|---|---:|---:|
| 0.8.35 exact baseline | 5.3769 / 5.1399 / 5.1092 ns | 5.1399 ns | 0 B |
| 0.8.36 candidate | 5.5552 / 5.1696 / 5.1706 ns | 5.1706 ns | 0 B |

最终差异为 +0.0307 ns（+0.60%），低于 5% 无回退门槛；三个逐进程差异也均低于 5%。首个“递增后再增加第三次状态检查”候选测得 5.3769 -> 5.7210 ns（+6.4%），已被否决。最终实现把全局计数发布移动到原有最终状态确认之前，因此维持原有两次状态读取与一次原子递增，同时关闭 Stop 漏计竞态。

其余修复位于连接清理、Build/Clone、握手或已删除失败入口，不改变正常序列化、transport、send-pump 或 compression 热路径。原始 fixture 与 exact worktree 保留在 `artifacts/performance/0.8.36-admission-probe/` 和 `artifacts/performance/0.8.36-baseline/`。

组合门禁为非增量 Release 0 warning/error、Generator 108/108、Unit 483/483、Integration 240/240。120 秒共享内存 Chaos 为 846,971 success / 331,401 expected / 0 unexpected / 23 restarts，Client/Server Error 为 0、drain 与五项零指标通过；NativeAOT TCP smoke 通过，七个 0.8.36 包预提交打包通过。
