# D 方向同步域拆分：真实 RPC 数据独立复核

复核日期：2026-10-11 UTC。复核人角色：并行独立审查；未调用主分析器，也未修改生产代码、collector 或主分析脚本。以下通过独立离线脚本从原始 report / CSV 重算；这次复核没有主动重跑性能实验。本文由原始档案 `evidence/new/d-screen-independent-review.zh-CN.md` 整理进入仓库，全部数值表分别保留 V1 / V2 的测量归属。候选定义、最终 No-Go、局部 Native 执行和归档恢复说明见[方法与决策报告](directional-control-and-methods.zh-CN.md)；全部原 ZIP、run、source、binary hash 见[固定证据索引](directional-artifact-index.json)。

## 1. 当前筛选结论

**V1 的 D 生产晋级建议为 No-Go：没有得到跨 JIT / NativeAOT 的、长流高并发下稳定 ≥5% 完整 RPC 改善；JIT PGO ON 还出现多个 CPU、分配和尾延迟同时退化的场景。** 这不是说所有同步域拆分永远无益；它限定于本次固定 D 实现、V1 harness、测试机与已测负载。D 也不是 B2 / B3，不能拿 D 的小回退证明全部 grant / ready-writer 方案不可能成功。

最关键的证据是：

1. NativeAOT `Duplex 1000×16 B c128`：TCP 四对吞吐变化为 +1.69%、+0.54%、−0.40%、+3.76%，中位数 +1.12%；SharedMemory −0.59%、+2.34%、+1.21%、+4.57%，中位数 +1.77%。对应 A/A 吞吐波动已分别达到 ±3.35% / ±3.40% 的量级，不能据此声称稳定增益。TCP CPU/RPC 四对均减少 1.59%–3.35%，这是值得保留的较小正面信号，但尚不足整体 Go。
2. NativeAOT 唯一四对中位数 ≥5% 的格子是 TCP `Duplex 100×4096 B c8`，中位数 +9.51%；四对却是 +19.34%、−0.33%、+25.34%、−2.04%，CPU/RPC 中位数 +0.65%、P99 +9.77%。两次大增益伴随 baseline 213 / 270 ms 的极尾，不能只挑这两对证明调度收益。
3. JIT PGO ON 的 SharedMemory `Duplex 1000×16 B c32`：吞吐 −6.47%、CPU/RPC +4.88%、B/RPC +20.00%、P99 +19.45%；`Duplex 100×4096 B c8` 四对均退，中位数 −3.80%，CPU 四对都增加；`Server 1000×16 B c128` 四对均退，中位数 −3.92%，CPU 四对都增加。不能因另一运行时小幅改善忽略这些结果。

V2 已在第 11 节按自己的 measurement source hash 独立增补。第 1–10 节所有数字仍只属于 V1；即使 D controller 字节相同，V1 / V2 的 lifecycle / measurement scope 改动仍不允许混为八对或跨版本汇总均值。V2 复核后仍建议 D 性能 No-Go，原因见第 11.6 节。

## 2. 输入、执行身份与独立复核方法

原始来源：[Actions run 38164814650](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650)，本地原件 `evidence/new/38164814650/{nativeaot,jit-pgo0,jit-pgo1}`。

| 项目 | V1 身份 |
| --- | --- |
| baseline | `f8ba89121c87a6410a0139c44d7db30eab877f41` |
| D candidate / measurement recipe | `a0dc552fe817780cc370329e2ef08355ddc6f82a` |
| measurementSourceSha | `12e46fb788f9e7ff11666afdcd2b55e77021abcbaffff571606452f99eb4a5dc` |
| baseline Runtime source tree | `007c7f8b408e7c492ca3298d1e10dab92afa8a4f` |
| D Runtime source tree | `1418036cea296b02c0bd856bdc6de7e531c071d1` |
| baseline controller SHA256 | `7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2` |
| D controller SHA256 | `a9cad11d9f80d8aedce4d412ada02ade32cd8048e0cfe133ad469d029eaf658d` |
| NativeAOT baseline ELF SHA256 | `02b5fc07bf2a09fb3555b96e64a83d38f0f4bc7d4e34b9eaa3585d060c9d9de1` |
| NativeAOT D ELF SHA256 | `6c714749bc665330b8f36d3345b0a0470edab51bcffd32f6be0dff402532146d` |
| JIT baseline DLL SHA256，两种 PGO 相同 | `b6e9a17f87301a5168f9b6b85108484ff3014e077b7e67fc57779861ccfa1474` |
| JIT D DLL SHA256，两种 PGO 相同 | `b561dc225e3aa8e8da1b1bf06b3bb39e6257952f255ee2907ff05a98a4ff7f31` |

独立脚本现交付为 [`recompute_d_screen_independent.py`](../../../eng/issue735/analysis/recompute_d_screen_independent.py)，原始副本仍保留在 `evidence/new/d_screen_independent_review.py`。它完成：

- 读取各 runtime 的 `screen-plan.json` 和原始报告，核对每个报告的 baseline / candidate / runtime tree / measurement hash / controller hash / SendPump hash / compiled mode / binary hash。
- 对下载的四个独立实际二进制重新 SHA256；两种 PGO 使用同一对 DLL；NativeAOT 的编译标记为 true 且 `IsDynamicCodeSupported/Compiled=false`。所有报告显示 `productionScheduler=dev-SendPump`、`b3ReadyWriterEnabled=false`，没有静默 B3 路径或改名。
- 从每一份 `.samples.csv` 的起止时间戳重算 latency，核对 latency_ticks、latency_us、successfulRpcs 及四个 nearest-rank quantile。432 份原始报告 / CSV 全部一致。
- 重算 `successfulRpcs / elapsedSeconds`、`cpuTotalMicroseconds / successfulRpcs`、`allocationBytesTotal / successfulRpcs`，全部与报告一致。
- 每 cell 使用 r0 的 A-B / B-A 与 r1 的 B-A / A-B，相邻四对；单对变化为 `100×(D/A−1)`。表中每项是四个百分比的中位数，不是把所有 latency 样本合并再取 quantile。
- A/A 独立取两轮相邻 right / left 比值，不并入 A/B 的 baseline 池。两轮 A/A 只能刻画已见噪声，不能估计精确置信区间，也不能当作每个不同 workload 都有同一噪声。
- 在独立重算完成后才对照主 `*-recomputed.json`：全部已比较 pair metric 数值 0 差异；没有调用或导入主分析器代码。

每个 runtime 128 个 A/B 报告 + 16 个 A/A 报告 = 144；共 432，所有 performance process exit 0，未缺 cell，没有合并失败进成功样本。离线完整复核值保存在 `d-screen-independent-v1.json`，便读摘要在 `d-screen-independent-v1-summary.txt`。

同机匹配的适用范围是同一个 Actions job 内的 A/B 与 A/A。不能仅凭相同 runner hostname 推断三个 runtime job 或 V1 / V2 使用同一台物理机器；本报告不跨 job 比较绝对 RPC/s，也不合并其进程样本。

## 3. 工作负载和 CPU / allocation / latency 的作用域

V1 是实际生成式 `IBenchmarkRpc`、SharpPack、TCP loopback / SharedMemory，同进程 client + server + 完整 byte / item-count validation。所有报告保留默认 Protocol v2、1 MiB stream window、16 MiB connection window、8 MiB send-queue budget、4 MiB pre-credit serialized budget、Balanced 默认 Flush；无压缩 provider。始终验证前后同一 physical session、pending/active calls 和 streams 归零，并在 transport Dispose 成功后才输出 passed。

源码依据：[Program.cs@a0dc](https://github.com/SunSi12138/SharpLink/blob/a0dc552fe817780cc370329e2ef08355ddc6f82a/test/SharpLink.FlowArchitectureEvidence/Program.cs)，[EvidenceEnvironment.cs@a0dc](https://github.com/SunSi12138/SharpLink/blob/a0dc552fe817780cc370329e2ef08355ddc6f82a/test/SharpLink.FlowArchitectureEvidence/EvidenceEnvironment.cs)。

**作用域限制应与结果一起展示：**

- CPU 是进程级 client + server + validation + GC 的 CPU/RPC，不是单端 CPU、纯 flow-controller CPU 或 core utilization 百分比。allocation 同样是进程全线程测量区间总分配，不是 `GetAllocatedBytesForCurrentThread`。
- 持久 worker、closure、样本数组、connection 建立和 ≥5 秒 warmup 在测量前；每个 worker closed-loop、一次仅一个 in-flight RPC，固定 operations / worker；client RPC 完成时记录单请求 latency。
- V1 的主要 CPU / allocation / elapsed 截止在 allWorkers 完成；后续 WaitForIdle + 同连接 probe 的成本不在主要 CPU/B/RPC 中，仅另有 `settlementAndProbeMicroseconds`。teardown 也在计时外。通过 settlement 不是把 settlement CPU 自动算入 RPC。
- `requestTimeout` 沿原 BenchmarkEnvironment 禁用，SharedMemory heartbeat 沿原基准为 5 min / 10 min；有 collector 60 秒总进程上限。两臂一致，且完整保留记录，但这不是 production 默认 deadline / cancel 压力验收。
- 所有运行记录 processorCount=4、taskset 0–3、ThreadPool minimum=32、workstation GC、.NET 10.0.12；JIT `TieredCompilation=1`，PGO 分别 0 / 1。每进程 warmup 至少 5 秒，`tierStabilizationVerified=false`，不能声称排除了所有 tier / GC / scheduler transient。
- Duplex 的 `logicalItems=RPC×N`，同时实际单 RPC 有 2N 个 stream payload items；报告额外保留后者。CPU/logical item 不应叫每个 wire DATA frame 的 CPU。
- 无 controller lock / CAS、ready commands、真实 queue depth / prepared budget peak / wire data frame counters；不能从此次吞吐变化得到精确协调分解，不能把 TP completed work item 数当成 writer handoff 次数。

## 4. NativeAOT V1：全部 16 个 cell

以下都是四对 `D/A−1` 的百分比中位数；吞吐正为改善，CPU/B/latency 负为改善。最后一列给出全部四对吞吐，防止中位数遮住方向不一致。

| workload | RPC/s Δ% | CPU/RPC Δ% | B/RPC Δ% | P99 Δ% | P99.9 Δ% | 四对 RPC/s Δ% |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| sharedmemory-Client-1000x16-c128 | +0.37 | -0.30 | +1.30 | -0.16 | -8.75 | +0.17, -2.08, +2.90, +0.58 |
| sharedmemory-Duplex-1000x16-c128 | +1.77 | -1.16 | +1.23 | -2.21 | -7.31 | -0.59, +2.34, +1.21, +4.57 |
| sharedmemory-Duplex-1000x16-c32 | +0.72 | -0.71 | +5.32 | -6.45 | -16.11 | +3.14, +1.38, +0.07, -2.15 |
| sharedmemory-Duplex-100x16-c1 | +1.47 | -1.49 | -0.16 | +0.23 | +3.98 | +4.08, +1.18, +1.31, +1.62 |
| sharedmemory-Duplex-100x4096-c8 | +1.56 | -1.92 | -0.38 | -9.66 | -37.91 | -0.42, -0.12, +11.15, +3.24 |
| sharedmemory-Server-1000x16-c128 | +0.79 | -1.29 | +2.51 | -0.29 | +1.25 | -0.67, +4.14, -3.27, +2.25 |
| sharedmemory-Server-1x16-c1 | -0.49 | +0.57 | +0.20 | +2.69 | +3.81 | +1.81, +0.32, -1.30, -1.61 |
| sharedmemory-Unary-1x16-c1 | -0.07 | -0.00 | -0.09 | -2.15 | +0.49 | +2.37, -0.05, -0.53, -0.09 |
| tcp-Client-1000x16-c128 | +0.39 | -0.34 | -0.43 | +0.94 | -2.19 | -0.73, +0.85, -0.07, +3.81 |
| tcp-Duplex-1000x16-c128 | +1.12 | -1.81 | -0.16 | +0.57 | +2.22 | +1.69, +0.54, -0.40, +3.76 |
| tcp-Duplex-1000x16-c32 | -1.03 | +0.44 | -0.78 | +6.78 | +3.78 | -2.96, +1.68, -4.07, +0.90 |
| tcp-Duplex-100x16-c1 | +0.82 | -1.17 | -1.77 | +2.24 | -22.28 | +1.14, +1.17, +0.51, -1.47 |
| tcp-Duplex-100x4096-c8 | +9.51 | +0.65 | +1.17 | +9.77 | -41.25 | +19.34, -0.33, +25.34, -2.04 |
| tcp-Server-1000x16-c128 | -1.14 | +0.01 | -0.35 | -0.96 | -0.75 | +0.11, -2.38, +3.11, -2.97 |
| tcp-Server-1x16-c1 | +2.61 | -3.79 | -0.02 | -2.38 | +4.06 | -0.91, +3.03, +2.19, +4.75 |
| tcp-Unary-1x16-c1 | -1.87 | +1.03 | +0.00 | +1.34 | +0.33 | -7.90, -1.44, +1.95, -2.31 |


### 4.1 不能把 NativeAOT TCP 4 KiB 的 +9.51% 叫稳定收益

这个 cell 每报告仅 1024 RPC。四对具体证据：

| pair | baseline / D RPC/s | CPU/RPC Δ% | P99 Δ% | baseline / D P99.9 ms |
| --- | --- | ---: | ---: | --- |
| r0 AB | 1035.731 / 1236.092 | +5.01 | +6.81 | 213.353 / 19.525 |
| r0 BA | 1343.332 / 1338.906 | −0.94 | −6.55 | 14.524 / 15.737 |
| r1 BA | 1002.209 / 1256.135 | +2.25 | +12.74 | 270.268 / 18.190 |
| r1 AB | 1278.546 / 1252.435 | −0.95 | +45.11 | 11.337 / 17.970 |

两份 baseline 有极长 P99.9，确实拖低其 throughput；这是真实保留的现象，不能删作异常值。现有数据也不能排除 D 降低某类偶发 stall 的可能，但没有该 workload A/A、TCP advertised-window / Recv-Q、credit timeline 或锁 profile，无法区分实现因果、GC / ThreadPool / transport transient。D 在另外两对吞吐回退，P99 三对恶化，CPU 两对增加，所以该格子不能独自支持生产 Go。

### 4.2 长流 c128 的真实正面信号及限制

TCP 的四对 CPU/RPC −1.99%、−1.59%、−1.64%、−3.35% 是方向一致的较小 CPU 改善；SharedMemory CPU −0.86%、−1.45%、+0.03%、−3.11%。应保留这两个事实，不以“平均没到 5%”否认所有小收益。

但是对应 TCP / SharedMemory 吞吐中位数只有 +1.12% / +1.77%，仍在同 workload A/A 已观察波动量级内。TCP 最后一对虽 throughput +3.76%，P99 从 320.251 ms 升至 442.872 ms（+38.29%），P99.9 从 333.617 ms 升至 460.706 ms（+38.09%）。只报四对 P99 中位数 +0.57% 会隐藏这次尾延迟恶化，因此 JSON 保留每对每指标的绝对值与变化。

## 5. JIT PGO OFF V1

| workload | RPC/s Δ% | CPU/RPC Δ% | B/RPC Δ% | P99 Δ% | P99.9 Δ% | 四对 RPC/s Δ% |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| sharedmemory-Client-1000x16-c128 | -1.22 | +1.48 | +0.80 | +0.77 | -5.42 | -4.80, -1.17, -1.27, -0.30 |
| sharedmemory-Duplex-1000x16-c128 | +0.71 | -0.16 | +2.83 | +1.84 | +2.65 | +0.22, +2.03, +1.19, -1.75 |
| sharedmemory-Duplex-1000x16-c32 | +1.81 | -1.09 | -6.94 | -3.78 | -13.81 | +3.36, -4.94, +6.67, +0.26 |
| sharedmemory-Duplex-100x16-c1 | +1.79 | -1.64 | -1.40 | +1.19 | -5.52 | +3.31, +0.27, -3.32, +3.88 |
| sharedmemory-Duplex-100x4096-c8 | -2.72 | +2.46 | -0.25 | +10.06 | +61.32 | -3.01, -2.65, -2.79, +0.35 |
| sharedmemory-Server-1000x16-c128 | +1.20 | -1.50 | -0.57 | -3.39 | -1.46 | +0.34, +2.05, +5.00, -0.71 |
| sharedmemory-Server-1x16-c1 | +0.97 | -1.39 | -0.15 | -1.34 | -1.40 | +1.48, +3.71, +0.47, +0.31 |
| sharedmemory-Unary-1x16-c1 | +2.67 | -2.73 | +0.01 | -3.77 | -3.19 | +2.43, +3.19, +2.91, +0.07 |
| tcp-Client-1000x16-c128 | -1.63 | +1.52 | -0.62 | -0.23 | +1.69 | -2.49, -3.49, +4.28, -0.77 |
| tcp-Duplex-1000x16-c128 | -0.44 | +0.53 | +0.35 | -11.87 | -11.92 | -0.00, -2.04, -0.88, +1.29 |
| tcp-Duplex-1000x16-c32 | -0.06 | +1.35 | +1.59 | +4.22 | +5.21 | -3.44, +1.63, +0.46, -0.57 |
| tcp-Duplex-100x16-c1 | -0.42 | +0.17 | -0.50 | +1.03 | +68.71 | -3.75, -0.95, +0.11, +0.77 |
| tcp-Duplex-100x4096-c8 | +2.98 | -3.10 | -0.99 | -2.57 | -7.06 | +6.76, +3.40, +2.56, -1.65 |
| tcp-Server-1000x16-c128 | -0.14 | -0.99 | -3.07 | +5.07 | +4.20 | -0.73, -5.41, +6.59, +0.46 |
| tcp-Server-1x16-c1 | +0.29 | -0.85 | +0.02 | -0.43 | +0.16 | +0.29, +0.29, -1.09, +1.07 |
| tcp-Unary-1x16-c1 | +1.13 | -0.89 | -0.00 | -0.67 | -5.68 | -5.32, +0.17, +4.97, +2.10 |


PGO OFF 没有任何 cell 的吞吐中位数达到 +5%。c128 Duplex TCP −0.44%、SharedMemory +0.71%，近于小幅摆动；Client c128 两种 transport 的中位数都下降。SharedMemory 4 KiB Duplex median −2.72%、CPU +2.46%、P99 +10.06%、P99.9 +61.32%；尾部虽稀疏，也必须保留这个观测，不能只使用 TCP 4 KiB 的 +2.98%。

## 6. JIT PGO ON V1

| workload | RPC/s Δ% | CPU/RPC Δ% | B/RPC Δ% | P99 Δ% | P99.9 Δ% | 四对 RPC/s Δ% |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| sharedmemory-Client-1000x16-c128 | +0.40 | -0.36 | -0.51 | -3.84 | -7.94 | -1.37, +3.36, -2.52, +2.16 |
| sharedmemory-Duplex-1000x16-c128 | -1.41 | +0.63 | +5.97 | +8.15 | +5.31 | -1.69, -1.40, -1.42, +0.98 |
| sharedmemory-Duplex-1000x16-c32 | -6.47 | +4.88 | +20.00 | +19.45 | +27.23 | -6.85, -6.08, -7.19, +3.11 |
| sharedmemory-Duplex-100x16-c1 | +0.22 | -0.19 | -0.35 | +0.84 | +4.41 | +0.97, +0.84, -0.40, -1.20 |
| sharedmemory-Duplex-100x4096-c8 | -3.80 | +2.37 | +1.01 | +16.98 | +8.52 | -2.01, -4.54, -3.06, -7.52 |
| sharedmemory-Server-1000x16-c128 | -3.92 | +3.91 | +6.28 | +9.49 | +11.73 | -2.25, -5.89, -5.59, -0.78 |
| sharedmemory-Server-1x16-c1 | +0.18 | -0.16 | -0.00 | -0.02 | -2.20 | +1.06, +2.04, -0.70, -2.42 |
| sharedmemory-Unary-1x16-c1 | +0.80 | -0.81 | -0.06 | -0.71 | +1.04 | +0.18, -0.79, +2.23, +1.41 |
| tcp-Client-1000x16-c128 | +0.02 | +1.25 | -0.57 | +1.55 | +1.09 | -1.58, -7.39, +3.33, +1.61 |
| tcp-Duplex-1000x16-c128 | -1.73 | +1.48 | +0.95 | +6.18 | +5.78 | +0.89, -6.54, -3.84, +0.38 |
| tcp-Duplex-1000x16-c32 | +1.45 | -1.37 | -1.59 | -5.19 | -2.79 | +1.66, +1.24, +2.50, +0.43 |
| tcp-Duplex-100x16-c1 | -1.74 | +1.48 | +0.29 | +3.89 | +56.45 | -2.05, -1.40, -1.44, -3.12 |
| tcp-Duplex-100x4096-c8 | -2.51 | +2.51 | -0.17 | +3.53 | -2.58 | +0.85, -5.42, -1.36, -3.65 |
| tcp-Server-1000x16-c128 | -3.18 | +1.59 | +0.98 | +1.49 | +4.42 | -3.62, -2.74, +1.23, -5.12 |
| tcp-Server-1x16-c1 | -1.48 | +0.78 | +0.01 | +2.08 | +4.68 | -3.24, -0.32, -2.64, +4.83 |
| tcp-Unary-1x16-c1 | -3.64 | +2.48 | +0.00 | +2.13 | -0.57 | -5.74, -8.35, -1.54, +10.59 |


PGO ON 的突出负面结果是多指标同时变化：

- SharedMemory Duplex 1000×16 B c32：前三对分别 −6.85%、−6.08%、−7.19%，最后一对 +3.11%；CPU 前三对 +5.81%、+4.40%、+5.36%，最后 −3.94%；B/RPC 四对中位绝对增加 74,197.77 B。该 workload 没有专属 A/A，因此不能直接扣除 c128 的 A/A，但整体方向已不足支持 Go。
- SharedMemory Duplex 100×4096 B c8：四对 −2.01%、−4.54%、−3.06%、−7.52%，四对 CPU 都增加，median +2.37%；这是比“只有个别中位数略负”更强的停止信号。
- SharedMemory Server 1000×16 B c128：四对 −2.25%、−5.89%、−5.59%、−0.78%，四对 CPU 都增加；B/RPC median +6.28%、P99 +9.49%。
- TCP Duplex 100×16 B c1 也四对全负，median −1.74%；NativeAOT 的同格子小正向没有在 PGO ON 复现。TCP c32 长 Duplex 是四对全正，但仅 median +1.45%，不能抵消其他负面格子。

这些是 V1 已观察的对照差异；没有 allocation stack / scheduling profile 时，不能把全部变化直接归因到新 Lock、dictionary layout 或某种猜测的 cache-line sharing。

## 7. 同 workload A/A 噪声：不与 A/B 混池

每行分别为两轮 right/left 百分比，未平均。A/A 的 P99.9 大幅变化说明少量长尾样本的局限，不能使用它来自动“原谅”任何 candidate 回退；同样也不能忽略 A/A，把大小相近的小 A/B 正值称稳定收益。

| runtime / workload | RPC/s 两轮 Δ% | CPU/RPC 两轮 Δ% | B/RPC 两轮 Δ% | P99 两轮 Δ% | P99.9 两轮 Δ% |
| --- | --- | --- | --- | --- | --- |
| nativeaot / sharedmemory-Duplex-1000x16-c128 | +3.40, -1.59 | -3.41, +0.96 | -3.46, +8.06 | -0.89, +2.08 | -0.69, +1.90 |
| nativeaot / sharedmemory-Duplex-100x16-c1 | -0.67, +5.13 | +2.09, -4.74 | +1.08, -2.23 | -12.27, -2.08 | -5.86, -8.49 |
| nativeaot / tcp-Duplex-1000x16-c128 | +2.83, -3.35 | +1.53, +3.02 | -1.74, +2.12 | -52.45, +3.42 | -50.28, +5.06 |
| nativeaot / tcp-Duplex-100x16-c1 | +1.37, +1.02 | -0.61, -1.56 | +2.56, +1.18 | -3.72, +0.82 | -51.99, +43.38 |
| jit-pgo0 / sharedmemory-Duplex-1000x16-c128 | -3.44, +2.74 | +2.87, -1.98 | -1.48, -3.04 | +11.72, +5.30 | +9.54, -2.46 |
| jit-pgo0 / sharedmemory-Duplex-100x16-c1 | -3.49, -2.45 | +4.13, +2.41 | +0.41, -0.06 | +1.65, +1.66 | +15.37, -3.34 |
| jit-pgo0 / tcp-Duplex-1000x16-c128 | +4.84, -0.46 | -1.11, +1.26 | +2.47, +1.05 | -38.33, -1.27 | -35.89, -0.77 |
| jit-pgo0 / tcp-Duplex-100x16-c1 | -1.70, +1.30 | +1.71, -1.96 | +1.48, -1.47 | +2.91, -0.73 | -29.88, +23.48 |
| jit-pgo1 / sharedmemory-Duplex-1000x16-c128 | -1.05, -0.81 | +1.17, +1.06 | -7.43, -6.38 | -14.49, -11.15 | -13.42, -13.21 |
| jit-pgo1 / sharedmemory-Duplex-100x16-c1 | -0.15, -4.45 | +0.36, +4.70 | -0.57, -0.69 | -8.49, +0.59 | +4.63, +2.51 |
| jit-pgo1 / tcp-Duplex-1000x16-c128 | +0.84, +3.80 | -0.65, -3.98 | -1.72, +3.00 | +1.18, -2.66 | -1.26, -0.61 |
| jit-pgo1 / tcp-Duplex-100x16-c1 | -0.81, -0.53 | +0.86, +1.48 | +0.50, +0.29 | +3.83, -4.57 | -23.98, -16.04 |

只有 Duplex 100×16 B c1 与 Duplex 1000×16 B c128 在两种 transport 各有 A/A；其他 shape / concurrency / payload 没有专属 A/A。不能把本表当作它们各自已验证的噪声范围。

## 8. allocation、GC 与常驻 Lock 的不同作用域

独立 footprint 已实测 baseline constructor 400 B，D 448 B，额外 48 B = new Lock 40 B + controller 自身增加 8 B，来源：[run 38165866902](https://github.com/SunSi12138/SharpLink/actions/runs/38165866902)。它不等于这里任何 B/RPC 差值：

1. 此 RPC harness 在 connection 建立、probe 和 warmup 后才开始 CPU / allocation 计数，前后同一 physical session；这部分 constructor 分配在主要测量窗口之外。
2. footprint 的 prewarmed send / receive churn 没有额外分配，不代表不同调度时序下 process-wide RPC 分配完全相同。实际 pipeline waiter、capacity events、pool backing、GC / continuation 行为可能随交错变化；这里只列合理解释类别，不声称已测得其贡献。
3. 例如 PGO ON SM c32 的 +74,197.77 B/RPC 和 SM c128 的 +32,568.93 B/RPC，是过程级实测差异，不能说成 +48 B/conn 的放大，更不能把正负差异全部归为 pool“噪声”后删掉。
4. A/A 也存在分配差异：Native SM c128 −3.46% / +8.06%，PGO ON SM c128 −7.43% / −6.38%。因此只有四对、没有分配栈时，不应把 candidate B/RPC 差值当作静态对象大小公式。

计时区间 GC pause / wall time 的观测范围：NativeAOT 0.032%–38.69%，PGO OFF 0.020%–28.65%，PGO ON 0.024%–32.27%。不同 workload 的 allocation / GC 量级差异很大；最大值不用于对所有 cell 归因，但说明不能把总吞吐简单按 connection lock 数预测。原始 gen0/1/2、GC pause 和 TP work-item 计数都留在 JSON，可按单 cell / 单 pair 查阅。

## 9. 尾延迟、公平性与正确性门槛

每报告成功 RPC 数为 1024–32768；最关注的 c128 长流是 4096 RPC，P99.9 名义上尾样本仅 4.096 条，c8 4 KiB 则约 1.024 条。nearest-rank 已逐 CSV 核对，但这不能使统计置信度变高；不构造伪精确 P99.9 置信区间，也不把所有运行拼成同一 stationary population。

每个 cell 四对来自两个 ABBA / BAAB block；它们仍共享 runner / 时间段，不能当四个完全独立生产集群。单进程测量 wall time NativeAOT 0.323–8.738 s、PGO OFF 0.383–10.172 s、PGO ON 0.323–8.968 s；较短的 controls 对 OS / GC / tier 时序尤其敏感。

common wall-time fractions 的最小 Jain index 为 NativeAOT 0.95345、PGO OFF 0.94337、PGO ON 0.94268。所有固定 workload worker 最后都完成，未观察到 permanent worker starvation；这不证明 stream-credit-blocked FIFO、公平借还或异构流无饥饿，因为任务数量相同且没有针对这些竞态注入。最终 100% completion 时 Jain=1 本身是固定配额的结果，不能单拿它证明 scheduler 公平。

432 个 passed report 包含完整 payload / item-count、同 physical session 与 active/pending 清零、teardown 完成。因此它们是有效真实 RPC screen，不是只有 microkernel 或 smoke。另一方面，本矩阵没有空流、多 named streams、压缩、显式 WindowUpdate 周期 / 饱和控制、用户 / 服务端取消、deadline / consumer Dispose、Flush blocked teardown 和 lease ABA 注入。独立 lease 正确性研究的实际 red/green 后续结果见第 11.6 节；不从这组成功性能进程推导已经通过那些 gate。

## 10. 决策建议与 V2 增补规则

- **D 当前不应作为已验收性能优化发布。** NativeAOT 长流有约 1%–2% CPU / 吞吐信号，但未达到可重复的跨配置目标；PGO ON 若干长流 / 大消息方向存在多个一致的负面指标。
- **不要再调大窗口、queue、ThreadPool minimum、Flush 或超时来挽救这些数字。** 已有小范围证据没有支持追加新的生产性能层；只审阅已经计划好的 V2、更完整 lifecycle 计量和必要 correctness 结果。
- **不预设 V2 必须重现 V1 的每个数。** V2 harness 修正改变 measurement hash / settlement 口径，新的样本可独立强化或削弱某个候选结论，但必须保留 V1 原始负面结果；不得相加成八对、均值抵消或只选较好版本。
- **正确性失败优先于性能结论。** 若 lease / cancel / ownership 注入失败，先按实际 baseline / D attribution 处理，保留 red/green；此报告不提前给正确性 Go。
- **No-Go 范围有限而明确。** 它拒绝“当前 D 满足本任务生产性能验收”的说法，不拒绝所有未来 owner / grant 设计，也不支持恢复旧 #803。原始反证比表面成功更有价值。

## 附：独立派生产物

- [`recompute_d_screen_independent.py`](../../../eng/issue735/analysis/recompute_d_screen_independent.py)：只读原 artifacts 的独立离线重算；不调用 dotnet，不运行或改动 benchmark。使用显式 `--input` / `--output`；新增完整计划和退出码检查，保留原数值计算。
- `evidence/new/d-screen-independent-v1.json`：四对全部绝对值 / 百分比、A/A、逐报告 hash、原始 quantile / 分母验证、ELF / DLL 校验。
- `evidence/new/d-screen-independent-v1-summary.txt`：按 runtime 展示全部 cell。
- 本文件 `directional-independent-review.zh-CN.md`：解释、筛选结论及证据限制；V2 单独补充，不改写 V1。原档案副本和新的重算/provenance 均保留；命令见[分析工具说明](../../../eng/issue735/analysis/README.md)。


## 11. V2 独立增补：包含 observed settlement 的计量

### 11.1 执行身份和成功 / 失败保留

原件来源：[Actions run 38165237528](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528)，本地 `evidence/new/38165237528/{nativeaot,jit-pgo0,jit-pgo1}`。该 run 各 runtime 144 个报告、128 A/B +16 A/A，全部 performance process exit 0；独立再次核验所有 432 份报告身份、实际 binary SHA、逐 RPC timestamp / quantile、主要与 settled 分母恒等式，没有发现缺样本或数值不一致。与主分析已生成的 pair metrics 对照为 0 mismatch。

| 项目 | V2 值 |
| --- | --- |
| baseline | `f8ba89121c87a6410a0139c44d7db30eab877f41`，与 V1 相同 |
| D candidate / measurement recipe | `45bbe67c6920bbdac2bc5071e39d54a7e21e746c` |
| measurementSourceSha | `f9e722beda390c1ed982ab747023bea9af63575d1effc9136d6c1eb3d6734652`，与 V1 不同 |
| baseline / D runtime source tree | `007c7f8b408e7c492ca3298d1e10dab92afa8a4f` / `d60bc0721e9b9f6b2b15fe274d9328280bc5ffd9` |
| baseline / D controller SHA256 | 与 V1 完全相同，两臂仍只是 D 两域拆分 |
| NativeAOT baseline ELF SHA256 | `edab4fbb28c63f4db2edd388e11fec4979c929c4d2e23b890e2fb9f775162caa` |
| NativeAOT D ELF SHA256 | `445626082b35b658527f043fd16f441eadc57562e45f99e12935279c02564fe5` |
| JIT baseline DLL SHA256 | `769919f8ae28ebe1be7a6af66645cfbe9b14f0aeac69e902f3910d2d60912f2f` |
| JIT D DLL SHA256 | `7ccf1ed99f7fdb1567d353129aebf266fc035cfce17f6737fca774f538317e40` |

PGO ON / OFF 仍使用同一对 JIT DLL；AOT 仍是实际 ELF 执行且 dynamic-code 两标记 false；所有报告明确 B3 disabled、dev SendPump。除新增计量 / 故障清理外，窗口、队列限制、workload、affinity、GC 模式、Flush 与 codec 控制保持所报告的配置。

V2 源码：[Program.cs@45bbe67](https://github.com/SunSi12138/SharpLink/blob/45bbe67c6920bbdac2bc5071e39d54a7e21e746c/test/SharpLink.FlowArchitectureEvidence/Program.cs)，[EvidenceEnvironment.cs@45bbe67](https://github.com/SunSi12138/SharpLink/blob/45bbe67c6920bbdac2bc5071e39d54a7e21e746c/test/SharpLink.FlowArchitectureEvidence/EvidenceEnvironment.cs)。新版本保留 completed 边界，另增加 observed call / stream settlement 后的 elapsed / CPU / allocation；故障路径保留首个异常并停止 / 收束 sibling workers。这不是对旧 run 的静默重标记。

### 11.2 settled 口径的实际成本并没有挽救性能差异

`settledBoundaryDefinition=measured-rpcs-plus-observed-call-stream-settlement`。它包含 WaitForIdle polling 与中间 counter 观察，仍明确排除 raw-sample file I/O、session probe、report formatting、transport disposal；不能称为从连接建立至完全销毁的全生命周期成本。

| runtime | completed→settled µs，min / median / max | 额外 CPU µs/RPC，median / max | 额外 B/RPC，median / max |
| --- | --- | --- | --- |
| NativeAOT | 65.687 / 115.571 / 204.262 | 0.10144 / 0.45215 | 0 / 0.734375 |
| JIT PGO OFF | 70.266 / 144.352 / 220.552 | 0.12158 / 0.55566 | 0 / 0.453125 |
| JIT PGO ON | 89.109 / 164.279 / 857.465 | 0.12463 / 0.63574 | 0 / 0.296875 |

这是正常成功负载的实测，不证明错误 / cancel 分支也如此便宜。将同一 pair 从 completed 指标换成 settled 指标，所有 V2 格子中吞吐百分比变化的最大差为 **0.023224 个百分点**，CPU/RPC 为 **0.021808 个百分点**，B/RPC 为 **0.001334 个百分点**。因此本组正常负载里，遗漏的短 settlement 段不是之前数个百分点乃至几十个百分点差异的解释。它也不允许将 V2 与 V1 直接池化，因为其余 harness 故障收束与 compiled image 身份已改变。

以下 V2 三张表的 RPC/s、CPU/RPC、B/RPC **使用 settled 口径**；P99 / P99.9 仍按每次 RPC validated completion latency，和 V1 定义相同。所有百分比都是四个相邻 pair 单独变化的中位数，四对列也使用 settled 吞吐。

### 11.3 NativeAOT V2

| workload | settled RPC/s Δ% | settled CPU/RPC Δ% | settled B/RPC Δ% | P99 Δ% | P99.9 Δ% | 四对 settled RPC/s Δ% |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| sharedmemory-Client-1000x16-c128 | -1.97 | +1.90 | -0.32 | -0.09 | -0.39 | -0.21, -2.11, -1.83, -2.62 |
| sharedmemory-Duplex-1000x16-c128 | +0.79 | -2.20 | +5.88 | -2.28 | -2.01 | +0.16, +1.14, +0.45, +4.25 |
| sharedmemory-Duplex-1000x16-c32 | -1.76 | +2.01 | -3.78 | +5.96 | +14.42 | +2.24, -1.27, -2.91, -2.25 |
| sharedmemory-Duplex-100x16-c1 | -1.17 | +1.35 | +0.59 | +0.45 | +2.40 | -1.68, -1.29, -0.59, -1.06 |
| sharedmemory-Duplex-100x4096-c8 | -3.51 | +1.97 | +0.42 | +7.66 | -14.18 | -5.08, -0.38, -4.08, -2.94 |
| sharedmemory-Server-1000x16-c128 | -0.38 | +0.02 | -3.31 | -1.43 | +0.82 | -1.03, -2.56, +2.15, +0.27 |
| sharedmemory-Server-1x16-c1 | -0.08 | +0.17 | -0.04 | -5.01 | -2.59 | -1.89, +0.50, +0.05, -0.22 |
| sharedmemory-Unary-1x16-c1 | +0.46 | -0.35 | -0.02 | -0.82 | +0.80 | +0.46, +10.80, -0.47, +0.45 |
| tcp-Client-1000x16-c128 | +0.63 | -0.98 | -0.80 | +0.38 | -0.46 | +2.12, -0.86, -2.17, +2.99 |
| tcp-Duplex-1000x16-c128 | -0.51 | -0.32 | +0.16 | +2.37 | +2.33 | -0.80, -1.08, -0.22, +0.16 |
| tcp-Duplex-1000x16-c32 | +2.55 | +0.42 | -1.84 | -37.28 | -38.08 | +6.86, -1.75, +9.20, -5.93 |
| tcp-Duplex-100x16-c1 | +0.11 | -0.01 | -0.11 | +0.04 | -6.13 | -0.29, -0.14, +2.60, +0.36 |
| tcp-Duplex-100x4096-c8 | +1.31 | -2.43 | +0.35 | -14.03 | -3.51 | +2.03, +1.30, -24.42, +1.33 |
| tcp-Server-1000x16-c128 | +4.02 | -2.50 | -5.97 | -2.32 | -2.50 | +0.89, +4.79, +3.24, +7.91 |
| tcp-Server-1x16-c1 | -1.11 | +1.75 | +0.02 | +0.70 | +4.32 | -0.89, +1.63, -2.31, -1.34 |
| tcp-Unary-1x16-c1 | +0.60 | +0.12 | +0.00 | +0.41 | +2.48 | +8.00, +1.29, -0.09, -2.40 |


NativeAOT V2 的 c128 Duplex TCP / SharedMemory 只有 −0.51% / +0.79%，没有复现 V1 的稍大但仍不足门槛的 +1.12% / +1.77%。SharedMemory c128 CPU −2.20% 值得保留，但 B/RPC +5.88%、吞吐不到 1%。TCP Server c128 四对都正、median +4.02%，CPU −2.50%、B −5.97%，是另一项真实小范围正面信号；它不能替代其他 runtime / shape 的验收。

更关键的是旧 V1 的 TCP 4 KiB 峰值在 V2 出现反向极尾：r1 BA 的 D throughput −24.42%、CPU −3.00%，P99.9 从 baseline 17.527 ms 增至 D 258.059 ms（+1372.32%），而其他三对约 +1%–2%。这说明前述“200 ms 等待只发生在 baseline，所以 D 稳定修好”的解释不成立。原因仍须保持未知：没有该轮 socket / credit timeline，不能称信用死锁；全部 RPC 最终成功也不能把这次停顿删去。

### 11.4 JIT PGO OFF V2

| workload | settled RPC/s Δ% | settled CPU/RPC Δ% | settled B/RPC Δ% | P99 Δ% | P99.9 Δ% | 四对 settled RPC/s Δ% |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| sharedmemory-Client-1000x16-c128 | +0.88 | -0.76 | -5.01 | -2.11 | +1.74 | -0.19, +0.64, +1.11, +2.12 |
| sharedmemory-Duplex-1000x16-c128 | -0.80 | +0.02 | -2.08 | +0.98 | +3.19 | +1.30, -12.88, +1.88, -2.90 |
| sharedmemory-Duplex-1000x16-c32 | -1.09 | +1.16 | +2.68 | +24.68 | +21.61 | +2.35, -0.99, -1.19, -3.40 |
| sharedmemory-Duplex-100x16-c1 | +0.61 | -0.48 | +0.63 | +1.10 | +0.96 | -0.31, -5.81, +5.07, +1.54 |
| sharedmemory-Duplex-100x4096-c8 | +1.89 | -1.49 | -0.31 | -5.80 | -7.45 | +1.96, +6.34, +0.52, +1.83 |
| sharedmemory-Server-1000x16-c128 | +0.14 | +0.18 | -1.21 | +0.60 | +0.11 | +1.10, -0.81, +2.76, -3.01 |
| sharedmemory-Server-1x16-c1 | -0.70 | +0.75 | +0.24 | +9.80 | +8.25 | -2.95, -1.10, -0.30, +1.19 |
| sharedmemory-Unary-1x16-c1 | -0.20 | +0.07 | +0.11 | +0.89 | +0.45 | +0.78, -0.16, -1.18, -0.24 |
| tcp-Client-1000x16-c128 | +0.73 | +0.41 | -0.19 | -3.89 | -4.36 | +3.49, +0.93, -0.07, +0.54 |
| tcp-Duplex-1000x16-c128 | +0.51 | -0.73 | -1.70 | +34.13 | +35.36 | +0.03, +1.66, +0.98, -5.04 |
| tcp-Duplex-1000x16-c32 | +1.29 | -0.02 | +6.51 | -29.05 | -28.69 | -6.58, -1.78, +5.26, +4.36 |
| tcp-Duplex-100x16-c1 | +2.39 | -2.04 | -0.99 | -1.06 | -26.59 | +3.16, +3.22, +1.63, +1.41 |
| tcp-Duplex-100x4096-c8 | -0.46 | +0.10 | +0.34 | -16.15 | -0.24 | -1.69, +5.89, -18.02, +0.78 |
| tcp-Server-1000x16-c128 | +1.40 | -1.11 | -5.15 | +2.51 | +4.48 | -36.60, +1.41, +1.39, +2.69 |
| tcp-Server-1x16-c1 | -0.84 | +0.75 | -0.01 | +1.54 | +0.63 | -1.43, -0.26, +2.33, -2.01 |
| tcp-Unary-1x16-c1 | +1.53 | -0.70 | +0.00 | -3.33 | -8.60 | +2.30, -1.55, +2.43, +0.75 |


PGO OFF 的 c128 Duplex TCP / SharedMemory 为 +0.51% / −0.80%。TCP Server c128 的 median +1.40% 不能掩盖第一对：D 1235.042 → 782.988 RPC/s（−36.60%），P99 135.017 ms → 1728.183 ms，P99.9 140.634 ms → 1730.571 ms；同时 CPU −1.20%、B −8.82%。这是大等待 / 停顿的真实观测，和纯 CPU 热路径变慢不是同一个现象。原退出码为 0，不构成永久信用死锁证据，也不构成足够好的尾延迟验收。

### 11.5 JIT PGO ON V2

| workload | settled RPC/s Δ% | settled CPU/RPC Δ% | settled B/RPC Δ% | P99 Δ% | P99.9 Δ% | 四对 settled RPC/s Δ% |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| sharedmemory-Client-1000x16-c128 | -1.66 | +1.69 | -1.16 | -3.37 | +5.28 | -2.21, -3.49, -1.12, +2.18 |
| sharedmemory-Duplex-1000x16-c128 | -0.76 | -0.04 | +13.46 | +2.48 | +2.61 | +0.84, -2.37, -4.91, +1.12 |
| sharedmemory-Duplex-1000x16-c32 | -0.44 | +0.90 | +10.69 | -10.21 | +0.76 | -1.05, +0.16, -5.82, +5.99 |
| sharedmemory-Duplex-100x16-c1 | +2.03 | -1.96 | +0.00 | -2.68 | -0.22 | +7.44, -1.03, +5.08, -2.08 |
| sharedmemory-Duplex-100x4096-c8 | +0.86 | -0.30 | -0.08 | +5.80 | +24.32 | -4.88, +5.11, +7.24, -3.39 |
| sharedmemory-Server-1000x16-c128 | -1.29 | +1.03 | -0.91 | +1.94 | +2.27 | -2.88, +0.53, -1.19, -1.40 |
| sharedmemory-Server-1x16-c1 | -0.48 | +0.59 | +0.02 | -2.38 | -0.66 | -2.31, -1.14, +4.62, +0.17 |
| sharedmemory-Unary-1x16-c1 | +1.26 | -1.49 | +0.01 | -1.54 | -0.82 | +2.46, +2.29, -0.54, +0.22 |
| tcp-Client-1000x16-c128 | +4.13 | -4.41 | -0.97 | -4.77 | -1.54 | +3.82, +9.22, +3.77, +4.44 |
| tcp-Duplex-1000x16-c128 | +1.45 | -2.16 | +1.74 | -1.15 | -0.43 | +0.25, +5.55, +2.52, +0.38 |
| tcp-Duplex-1000x16-c32 | +4.10 | +0.33 | +5.59 | -36.99 | -37.99 | +0.28, +10.78, +4.36, +3.84 |
| tcp-Duplex-100x16-c1 | -0.34 | +0.67 | -0.06 | +1.10 | +10.59 | +0.51, -5.01, -0.04, -0.64 |
| tcp-Duplex-100x4096-c8 | -3.11 | -2.31 | +0.97 | +18.33 | +29.96 | -22.15, -0.27, +3.50, -5.94 |
| tcp-Server-1000x16-c128 | -4.91 | +2.36 | +25.25 | +0.32 | +0.08 | -8.00, +1.09, -7.06, -2.75 |
| tcp-Server-1x16-c1 | -0.49 | +1.32 | -0.00 | -0.07 | -8.15 | +5.40, -0.52, -1.15, -0.45 |
| tcp-Unary-1x16-c1 | -4.72 | +4.35 | +0.00 | -0.02 | +3.23 | -8.89, +3.09, -5.17, -4.26 |


PGO ON TCP Client c128 四对 settled throughput +3.82%、+9.22%、+3.77%、+4.44%，median +4.13%；CPU −3.91%、−7.72%、−3.47%、−4.90%，median −4.41%，B/RPC median −0.97%。这是 V2 最清楚的正面 cell，不能只因不足 5% 就丢弃。另一方面 V1 同格子并未得到相同的稳定幅度，V2 SharedMemory Client c128 又是 −1.66%，所以仍不能证明可迁移的整体增益。

同 runtime 的 TCP Server c128 吞吐 −4.91%、CPU +2.36%，**四对 B/RPC 全增加，分别 +23.40%、+26.42%、+42.44%、+24.08%，median +25.25%**；绝对分配差 median +124,628.46 B/RPC。SharedMemory Duplex c128 throughput −0.76%、CPU 近零变化、B +13.46%（median +79,057.41 B/RPC）。D 的 constructor +48 B 仍在测量区间外，无法解释这些幅度。没有分配栈 / queue / cache 证据，不对来源作虚构归因。

TCP c32 Duplex 吞吐 median +4.10%、四对全正，但 CPU +0.33%、B +5.59%，并非全指标改善。其 P99 / P99.9 约 −37% 是保留的尾部现象，不能与其他 cell 的吞吐中位数跨格子组合成虚拟“最佳配置”。

### 11.6 V2 A/A、正确性新证据与最终筛选

V2 A/A 的两轮百分比仍单独列出。前三项用 settled 口径；不与 V1 或 V2 A/B 混池。

| runtime / workload | settled RPC/s 两轮 Δ% | settled CPU/RPC 两轮 Δ% | settled B/RPC 两轮 Δ% | P99 两轮 Δ% | P99.9 两轮 Δ% |
| --- | --- | --- | --- | --- | --- |
| nativeaot / sharedmemory-Duplex-1000x16-c128 | -0.59, -5.83 | +2.38, +4.64 | -3.42, +8.29 | -7.01, +10.59 | -11.24, +13.54 |
| nativeaot / sharedmemory-Duplex-100x16-c1 | -1.11, +2.02 | +1.16, -2.07 | -0.56, +1.52 | -0.49, -2.86 | +6.10, -0.57 |
| nativeaot / tcp-Duplex-1000x16-c128 | +1.32, -4.98 | +1.70, +4.19 | +4.50, +1.22 | -54.62, +4.22 | -55.36, +4.17 |
| nativeaot / tcp-Duplex-100x16-c1 | +0.10, -2.48 | -0.12, +0.31 | -1.76, -0.05 | +1.88, +7.51 | +10.59, +124.75 |
| jit-pgo0 / sharedmemory-Duplex-1000x16-c128 | +2.00, -1.00 | -3.00, +2.81 | +2.41, -0.68 | +10.00, -9.48 | +9.02, -9.83 |
| jit-pgo0 / sharedmemory-Duplex-100x16-c1 | +6.84, -1.22 | -6.47, +1.46 | -0.53, +0.35 | -3.05, -0.13 | +1.74, -21.82 |
| jit-pgo0 / tcp-Duplex-1000x16-c128 | +3.94, -4.76 | -2.68, +2.13 | -3.63, -0.29 | -2.21, +0.99 | -5.18, -1.72 |
| jit-pgo0 / tcp-Duplex-100x16-c1 | +1.07, +0.48 | -1.43, -0.89 | +1.13, -1.32 | +3.48, +1.39 | +80.09, +41.48 |
| jit-pgo1 / sharedmemory-Duplex-1000x16-c128 | -0.68, -0.93 | -0.28, +0.67 | +8.77, +3.88 | +4.44, -2.18 | +7.51, +1.87 |
| jit-pgo1 / sharedmemory-Duplex-100x16-c1 | -0.08, -8.23 | +0.20, +9.17 | +1.05, +0.74 | -3.17, +3.41 | -14.96, +2.68 |
| jit-pgo1 / tcp-Duplex-1000x16-c128 | -3.65, -1.95 | +5.47, +2.40 | -1.12, -0.45 | +3.80, -26.33 | +9.08, -21.97 |
| jit-pgo1 / tcp-Duplex-100x16-c1 | -0.22, -1.68 | -0.37, +2.53 | +0.61, +0.07 | +5.17, -8.80 | +20.91, +2.66 |

V2 也只有两个 Duplex workload 具专属 A/A，其余 cell 不应借用未经验证的噪声界。c128 NativeAOT 的 A/A throughput 在 TCP 最低约 −4.98%、SharedMemory 最低约 −5.83%；PGO ON c128 TCP A/A −3.65% / −1.95%。短 TCP Duplex NativeAOT A/A 的 P99.9 第二轮 +124.75%；四对 A/B / 两对 A/A 仍不足稳定估计极尾。V2 最小 Jain 为 NativeAOT 0.95869、PGO OFF 0.95765、PGO ON 0.93607，不存在本 screen 观测到的永久 worker 饥饿，但也不证明全部协议公平不变量。

**分别看 V1 和 V2 后，D 性能筛选继续 No-Go。** 不是机械以 5% 为唯一门槛：V2 TCP Client 的 CPU / throughput 小收益已完整保留；但关键 Duplex 的收益幅度与符号未在三运行时一致，整体存在 repeatable negative pairs、大额 allocation 差异和严重偶发尾部，未满足用户要求的综合生产改善。V2 全部 48 个 workload×runtime cell 的吞吐 median 都未达 +5%，且没有可据以解释反向波动的操作 counters。

并行正确性研究已经出现与此相互独立的新事实：[run 38166410894](https://github.com/SunSi12138/SharpLink/actions/runs/38166410894) 在固定原 baseline + 确定性 hook 中实际触发 `ReusedObjectMustRejectOriginalAcquire(True)` 失败，错误为旧 Acquire 被接受进 replacement generation，original-focused exit 2；修复 overlay 的 4 轮 focused 与 4 轮完整测试成功，后者每轮 2090 / 2090。原 `result.json`、`exits.json`、original-focused-r0.log 和 fixed-full-r0..r3.log 已另行核对。它定位的是 baseline 已有 lease-capture gap，**不能归咎为 D 新引入，也不能被 864 个成功性能报告掩盖**。该 correctness 修复不在 V1 / V2 性能二进制里，因此不得提前声称修复后的 RPC 性能等同于此报告。

该独立 correctness 修复已经提出为 [PR #849](https://github.com/SunSi12138/SharpLink/pull/849)；PR 的新增回归/原 CI 状态应以该 PR 的实际结果另行报告，不能借用本报告中的实验 overlay green 代替。对 D 保存实验 No-Go，不再往当前 D 叠加性能调参。若未来评估 A / B / C，须在固定包含修复的新基线上重新界定性能成本与安全所有权；本报告不授予任何新性能试验或协议 / 资源修改的默认成功判断。

V2 独立派生产物：`d-screen-independent-v2.json`、`d-screen-independent-v2-summary.txt`。两轮合计 864 个成功 report 是**两个独立 measurement-hash 层各自的数量相加**，不是 8-pair 汇总、同一 image 或整体性能显著性的样本量。


## 12. 交付时归档和执行边界校验

交付前重新查询 GitHub artifact 元数据，发现 V1 两个已有本地 JIT ZIP 不完整，均无法读取 ZIP 中央目录。它们原样保留，没有作为有效原 ZIP 交付。随后通过 connector 重新下载原 artifact 11712571300 / 11712845476，等待下载进程 exit 0；新的 ZIP 大小及 SHA256 与 GitHub digest 一致。各 1,091 个文件条目与此前分析的解压目录逐字节比较，共 2,182 项、**0 差异**，包含所有原 report / CSV、执行二进制与 stdout/stderr。没有替换测量样本或重新执行 benchmark。详细新旧 ZIP 摘要和条目比较保存在档案 `evidence/new/d-v1-zip-recovery-comparison.json`，简明记录见[固定索引](directional-artifact-index.json)。因此恢复的是原始 ZIP 保存完整性，不是用新样本覆盖失败。

交付 CLI 已分别对 V1、V2 全部 432 份报告重新执行，两个退出码均为 0。全部原身份、逐 pair 绝对值/百分比和统计数值保持一致；输出路径改为绝对路径，V1 新增三个显式 `null` 的 settled metadata 字段以标明未测，不虚构 settled 数值。输出位于档案 `evidence/new/d-deliverable-recomputed/`，附每次脚本/input/output SHA256 provenance。

另有当前容器的实际 NativeAOT generated RPC 可执行性验证：使用 V1 原 ELF，TCP A 与 B 各执行 Duplex 100×16 B、c1、256 个 measured RPC 后 exit 0；SharedMemory A 在 NamedPipe socket 创建阶段得到 `SocketException (13): Permission denied`，exit 1、没有测量样本，随后按计划停止，B 未执行。完整 command、hash、exit、partial 文件位于 `evidence/new/38164814650/nativeaot/local-execution/`。它既不是本报告原 Actions 144 个 Native 进程的重标记，也不进入两版合计 864 的数量或任何性能统计。此局部失败不抹去原 Actions SharedMemory 执行成功，也不能冒充本地 SharedMemory Native 验收。

**B2 的生产 pilot 尚未执行。** 本文的否决对象始终是该 D 实现的生产性能晋级，未获得 B2/B3/混合架构相对新正确性基线的真实 RPC 否定结论。
