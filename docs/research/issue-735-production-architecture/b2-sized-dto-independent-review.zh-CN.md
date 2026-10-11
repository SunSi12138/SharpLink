# 5cab B2 sized-DTO：JIT / NativeAOT 原始证据独立复核

## 阶段性判断：本次 pilot No-Go

**本次固定 B2 pilot 的生产晋级判断为 No-Go，停止继续扩大性能接入。** 两个 JIT 群体在默认资源配置的 SharedMemory / Server / 1,000×64 B / c128 预热中触发 `send_queue_capacity`：PGO OFF 失败在共同正确性 baseline A，PGO ON 失败在候选 B。两份原始产物中该进程的正式测量均为 0 RPC。这个事实证明当前固定 workload 会遭遇生产发送队列的保护性拒绝，不能单独归因为 B2，也不能作为成功样本删去。

此前成功的长流报告已证实 B2 实际激活：1,000×16 B 每 RPC 有 995 个 local item、4 次 owner refill、1 次初次授信；1,000×64 B 为 983 / 16 / 1。每个 JIT 的 18 个 eligible B 报告中，116,736 次激活均已 Dispose，first-probe fallback、exact-slow、unsent refund 计数均为 0。**把绝大多数后续 item 转为 stream-local admission 的目标确实实现了；现有两对 JIT 实际 RPC 对照尚未显示目标长流的稳定收益。**

NativeAOT 已实际发布执行，144/144 个计划进程成功，16 个 cell 各完成 4/4 个 A/B pair，另有 8 个 A/A pair。Native 的 12 个 eligible cell、两个 JIT 各 9 个已有完整相邻 pair 的 eligible cell，共 30 个观测 cell，没有一个吞吐 pair 变化的中位数达到 5%。这只是逐群体描述，不把三种 runtime 合并为一个统计样本。NativeAOT 的小幅正值、CPU 与尾延迟改善仍逐项保留；结合两份 JIT 默认资源失败和未兑现的完整 RPC 增益，证据不支持生产晋级。

这个判断不等于“所有 B2 都不可行”，也不是完整性能验收：JIT 每组仍仅 12 个 cell 各完成 2/4 个预声明配对，A/A 全未运行，失败之前的单臂和所有 NotRun 均保留。Native 的成功不覆盖 JIT 的红灯。

## 固定源与证据分类

- 实验运行：[Actions 38173700315，attempt 1](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315)。
- 候选：[5cabd4d7cc5ce9aa0a87424e36954e414e0912da](https://github.com/SunSi12138/SharpLink/commit/5cabd4d7cc5ce9aa0a87424e36954e414e0912da)。
- 共同正确性 baseline：[c7ba1768f31ce7219dc438dc6009347de6806ed5](https://github.com/SunSi12138/SharpLink/commit/c7ba1768f31ce7219dc438dc6009347de6806ed5)，包含独立 lease、snapshot、terminal-refund 修复；不把这些修复的效果归给 B2。
- 原始 dev：`f8ba89121c87a6410a0139c44d7db30eab877f41`，已包含 #840 / #841。
- 所有本报告三个 runtime 样本的 measurement content SHA256：`db0d2bce291742a67a8635516d0f2ff16b1faa35877ced6aa8667573f42a3c87`。
- 分类：**实际生产生成式 RPC，JIT PGO 0 / 1 与实际发布执行的 NativeAOT，各自同 job、同机交错 A/B。** 三个 runtime 逐组分析，均不是 research model 或 matched microkernel；Native 结果也不是 smoke test。

`identity-A/B.json` 的 18 项 measurement manifest 已逐项对照固定 Git 5cab 的真实字节，重新计算长度前缀 content digest。源作用域独立对照 496 / 497 个生产文件的 Git 树：A 相对共同 baseline 无性能改动，B 仅四个明确允许的 Runtime 文件变化。A/B 的 SendPump 文件均与 dev / c7 / 5cab 完全相同。Actions 中固定版本 stamper 自身检查编译目录每个工作生产 blob；本地审计不把 identity 记录冒称成第二次抓取 Actions 文件系统。

源审计：`evidence/new/38173700315/sized-independent-git-input-verification.json`（[原产物索引](b2-artifact-index.json)）。独立审计脚本：`evidence/b2-pilot/verify_sized_rpc_git_inputs_5cab.py`，SHA256 `0926bcf0cacbce2883f0930b46a10e1987f407ac442b1f23e06258abb8196d81`。

## 原始 ZIP 与失败覆盖

| 群体 | Job | Artifact | ZIP 字节 | GitHub digest / 实际 SHA256 | 成功 / 失败 / NotRun |
|---|---:|---:|---:|---|---:|
| JIT PGO OFF | 114572398471 | [11715317228](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715317228) | 17,847,587 | `a2ebc9b8c200abe2c9644f930066b68842f2659844f8d5d61a18ed75bd5cbd42` | 48 / 1 / 95 |
| JIT PGO ON | 114572398424 | [11715501334](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715501334) | 17,884,611 | `e6b9d11213c7baa1694effc388252b26243689572bc47b680fba5ae2a43f7a27` | 49 / 1 / 94 |
| NativeAOT | 114572398512 | [11716140730](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11716140730) | 63,196,829 | `a6599a3e404e33890146f18b9e4ed3dafa444acaf143d1b2eed81b3dfa4cf852` | 144 / 0 / 0 |

每份目录保留 `original-artifact.zip`、`github-artifact.json`、`download-verification.json` 与 `raw/`。ZIP size 与 GitHub digest 已核验，路径安全检查通过；JIT 的 557 / 564 个文件与 Native 的 1,068 个文件建立本地 SHA256 清单。**RPC ZIP 未提供独立 upstream artifact manifest；本地清单明确是下载后生成的校验清单。** 原运行成功生成的 `screen-plan.json`、`exits.json`、source identities、实际 binaries、所有 per-RPC samples / worker / fairness / failed worker 记录均保留。

失败 stems：

- OFF：`ab-r0-sharedmemory-Server-1000x64-c128-p0-A`，exit 1，wall `3.5045957565307617 s`。
- ON：`ab-r0-sharedmemory-Server-1000x64-c128-p1-B`，exit 1，wall `3.857510566711426 s`；同 cell 前一单臂 A 已成功，仍独立保留，不配上其他轮次 B。

两个 `samples/<stem>.txt.failure.txt` 均报告 `A persistent RPC worker failed before measurement`，inner exception 是 `Session send queue exceeded its 8388608-byte limit (send_queue_capacity)`。Stack 从 `PooledAsyncStreamDispatcher<T>.SlowMoveNextAsync:829` 经真实生成式客户端流、`GrantBenchmarkClient.InvokeAsync:98` 到 `RunWorkerAsync:273` 的 warmup 调用，再由 `ReadyOrFailureAsync:317` 传递。两份 `.samples.csv` / `.fairness.csv` 只有表头，0 行；`.workers.csv` 各有 128 行，`completed` 合计 0 / expected 4,096。此前 warmup 完成 RPC 合计分别为 738 / 886；固定 warmup 规则是每 worker 同时达到至少 2 次和至少 5 秒，因此不能按“仅 2×128 次预热”解释负载。

128 份 worker failure 首行分组：OFF 为 25 次 capacity、93 次 `Client is stopping`、10 次取消；ON 为 1 / 120 / 7。后续 shutdown 记录不是 128 次独立的 capacity 重现实验，首个失败仍以主 `.failure.txt` 为准。原失败审计：`evidence/new/38173700315/sized-jit-first-failures-independent.json`（[原产物索引](b2-artifact-index.json)）。

两个失败进程已完成实际 codec preflight：`CanExactSize=true`，encoded DTO item 64 B，TEXT 27 个 UTF-16 code unit，tag 29,531，uncompressed DATA frame 81 B。它们是实际 RPC 运行时资源拒绝，**不是编译失败、codec preflight 失败、activation 断言失败或 60 秒外部 watchdog 超时**。失败产物没有正式报告、实际 send-queue peak 或 settled snapshot；不能据其证明峰值恰好为某个推算值，也不能声称已经排除 leak、Flush starvation 或信用死锁。

精确源码入口：[SendPump.TryEnqueue](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/src/SharpLink.Runtime/RpcSession.SendPump.cs#L102-L128)、[warmup / failure handoff](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs#L256-L320)。完整队列拒绝因果边界见[独立源码归因](b2-queue-capacity-attribution.zh-CN.md)，其中精确区分 normal admission limit 8,372,224 B、总限额 8,388,608 B 与未测实际峰值。

## 独立复算方法与测量边界

第一条复算路径使用 `evidence/b2-pilot/independent_sized_rpc_pairs_5cab.py`，SHA256 `7853770335b25feb4e8f9f0ba49e70d7308b1fd297fc194ae2c63dc5501b3e64`：直接读取原始 key/value report 与每 RPC CSV，不 import frozen strict parser，不读取 Actions 的 paired-summary。逐 worker 检查 operation 连续、时间戳单调、`ticks=end-start`、实际操作数；独立重算 nearest-rank P50 / P95 / P99 / P99.9。共重新读取 OFF 512,000 / ON 516,096 / NativeAOT 1,196,032 个实际测量 RPC 样本。

第二条路径执行仓库冻结 `eng/issue735/recompute_b2_screen.py`，SHA256 `11db53225f8fe5c04f3a6a118b4594440f5e3fcd6d71ae68934ba82d4a475201`：原 source / binary / mode / payload / command / environment 校验、worker / fairness / raw sample 验证全部执行，两个 JIT partial population 和完整 Native population 都合法重算；failed / NotRun 均保留。两条路径的共同指标逐个相邻配对比较，OFF 323 / ON 322 / NativeAOT 870 个有效 A/B 数值完全一致（绝对或相对误差 ≤ 1e−12）；这个比较只是两条已完成复算的交叉检查，不是独立复算的输入。

配对固定为 r0=ABBA、r1=BAAB，每轮相邻 0/1 与 2/3，各自按 `(B/A−1)×100` 计算。只计算完整的 pair，取 pair 变化的中位数；不将单臂补上其他轮次或运行时的样本，不把进程内几十万 RPC 当作几十万个独立 A/B 实验。JIT 下表 cell 均是 **n=2 pairs / 计划 n=4**，A/A n=0；Native 表为 **n=4 pairs**，另有对应长流的 **n=2 A/A pairs**。不能给 JIT 补出噪声区间，也不把 Native 的两次 A/A 当作正式置信区间。

CPU / allocation 是两个 RPC 端点共用的整个进程的 CPU 时间、`GC.GetTotalAllocatedBytes` 增量。成功报告同时保留 RPC completed 与 session 已观测 call / stream settled 边界；settled 增量包含 idle 轮询与中间计数读取成本，不能冒称完整 SendPump / late WINDOW_UPDATE 全局静止。preflight 与前后 same-physical-session probe 不计入 measurement。Runtime 记录为 .NET 10.0.12 / X64 / Ubuntu 24.04.5，4 CPU affinity、ThreadPool minimum 32、workstation GC；JIT warmup 未证明所有 tier 已稳定 (`tierStabilizationVerified=false`)。

## JIT 部分配对结果（completed 边界）

以下吞吐正数表示提高，CPU / 分配 / 尾延迟正数表示变差。`Δ B/RPC` 是各相邻配对绝对分配差的中位数，分配 `%` 是各 pair 比率变化的中位数；baseline 分配变化较大时二者可能方向不同，不能相互替换。每行的原始两个 pair 和 min/max 均在独立 JSON 中保留。

### JIT PGO OFF

| 场景 | 吞吐 % | CPU/RPC % | 分配 % | Δ B/RPC | P99 % | P99.9 % |
|---|---:|---:|---:|---:|---:|---:|
| tcp-Unary-1x16-c1 | +6.17 | -4.18 | +0.01 | +0.09 | -0.98 | +0.51 |
| tcp-Server-1x16-c1 | -0.72 | +0.96 | +7.65 | +110.40 | +1.06 | -3.64 |
| tcp-Server-16x16-c8 | -4.71 | +3.25 | +2.61 | +72.36 | +5.44 | +7.99 |
| tcp-Server-1000x16-c128 | -4.31 | +2.08 | +10.47 | +17984.70 | +47.61 | +45.22 |
| tcp-Server-1000x64-c128 | +0.22 | -0.52 | -2.82 | +1169.13 | -2.88 | -5.24 |
| tcp-Client-1000x16-c128 | -6.84 | +4.65 | +37.59 | +200289.58 | +6.29 | +4.10 |
| tcp-Duplex-1000x16-c128 | -1.76 | +2.07 | +0.83 | +11932.53 | -26.48 | -20.38 |
| tcp-Server-100x4096-c8 | +3.48 | -2.57 | -0.81 | -3639.50 | +1.19 | +6.33 |
| sharedmemory-Unary-1x16-c1 | -1.84 | +1.86 | +0.21 | +3.74 | +4.74 | -1.52 |
| sharedmemory-Server-1x16-c1 | -2.83 | +3.57 | +4.95 | +119.79 | +1.84 | -5.57 |
| sharedmemory-Server-16x16-c8 | -4.38 | +5.26 | +3.88 | +126.75 | -3.24 | -35.21 |
| sharedmemory-Server-1000x16-c128 | -0.12 | -0.37 | -0.95 | -1392.84 | +44.27 | +45.41 |

原始独立复算：`evidence/new/38173700315/jit-pgo0-sized-dto/independent-raw-pairs.json`（[原产物索引](b2-artifact-index.json)）；严格复算：`evidence/new/38173700315/jit-pgo0-sized-dto/strict-recomputed.json`（[原产物索引](b2-artifact-index.json)）。

### JIT PGO ON

| 场景 | 吞吐 % | CPU/RPC % | 分配 % | Δ B/RPC | P99 % | P99.9 % |
|---|---:|---:|---:|---:|---:|---:|
| tcp-Unary-1x16-c1 | +1.43 | -1.43 | -0.03 | -0.26 | -0.98 | -1.88 |
| tcp-Server-1x16-c1 | -2.97 | +3.65 | +7.79 | +112.22 | +1.71 | +3.35 |
| tcp-Server-16x16-c8 | +0.13 | +0.96 | +3.32 | +87.62 | -8.38 | -8.99 |
| tcp-Server-1000x16-c128 | -4.16 | -0.71 | +10.99 | +16423.14 | +66.03 | +75.52 |
| tcp-Server-1000x64-c128 | -1.28 | +0.80 | -2.69 | -13880.44 | +5.79 | +5.59 |
| tcp-Client-1000x16-c128 | +13.01 | -6.53 | -30.17 | -222878.54 | -9.05 | -9.32 |
| tcp-Duplex-1000x16-c128 | -1.13 | +0.27 | -2.36 | -37277.98 | +18.55 | +20.08 |
| tcp-Server-100x4096-c8 | +0.33 | -0.77 | +0.59 | +2650.46 | -14.98 | -13.83 |
| sharedmemory-Unary-1x16-c1 | +0.64 | -0.98 | -0.03 | -0.53 | +1.41 | -0.73 |
| sharedmemory-Server-1x16-c1 | -1.52 | +1.80 | +4.78 | +113.57 | +1.51 | -4.32 |
| sharedmemory-Server-16x16-c8 | -3.81 | +3.08 | +3.88 | +109.83 | +7.26 | +3.85 |
| sharedmemory-Server-1000x16-c128 | -2.55 | +2.17 | -0.14 | -176.00 | +15.77 | +16.20 |

原始独立复算：`evidence/new/38173700315/jit-pgo1-sized-dto/independent-raw-pairs.json`（[原产物索引](b2-artifact-index.json)）；严格复算：`evidence/new/38173700315/jit-pgo1-sized-dto/strict-recomputed.json`（[原产物索引](b2-artifact-index.json)）。

### 如何解释这些部分结果

两个 JIT 的 TCP / Server / 1,000×16 B / c128 吞吐变化为 −4.31% / −4.16%，P99 为 +47.61% / +66.03%；SharedMemory 同类长流变化为 −0.12% / −2.55%，P99 为 +44.27% / +15.77%。这些是实际成功配对中的观测值，方向不支持预期的稳定 ≥5% RPC 收益；因为第二轮和 A/A 缺失，不能把此幅度当成准确总体效应或尾延迟长期分布。

1 item Server 的分配增量约 110–120 B/RPC，与独立 footprint 的每次 lease-context allocation 同量级，但这个对应不是因果分解。长流实际 B/RPC 有更大的差异；不能把它们直接归为 112 B lease 或 16 B controller 常驻差，也不能把不同结构测量直接相加后称为 RPC 解释。

两个 JIT 各 9 个 eligible cell 的吞吐中位数均为 2 正 / 7 负，0 个 ≥5%；四个表面正值 cell 的两个 pair 都是一正一负。例如 OFF TCP100×4,096 为 +7.638% / −0.677%，不能挑出前者作为晋级依据。

零激活 Client 对照的吞吐 / 分配在 PGO OFF 与 ON 出现反向大幅变化。零激活仅证明没有误报 grant 激活；B 仍是改变了 controller / bridge 的生产二进制，因此它既不是纯 A/A，也不能被一概叫作噪声。调度 / GC / 池行为与未激活路径的整合成本仍未被这个 partial screen 区分。

## NativeAOT 完整配对结果（completed 边界）

Native 原运行 [job 114572398512](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/job/114572398512) 实际成功，原 ELF 随 ZIP 保留。A ELF SHA256 `df0621ea0fe6f0b326621742e2cd8408cef271e466ebba7904f42698ab10c657`，B ELF SHA256 `64e9f6d91d7db8891cc0047650770f3dee15410f6a5b46415c7f06638e515c4b`；ELF header、实际执行 command、每报告 `builtForNativeAot=true` / `dynamicCodeSupported=false` / `dynamicCodeCompiled=false` 与 executable hash 均核验。没有以发布成功或 smoke test 代替真实 RPC 执行，也没有为了复核而再跑一轮性能。

以下每 cell 四个相邻 pair 全在原 plan 中，source / measurement / physical session / payload / credit activation 的检查均通过。

| 场景 | 吞吐 % | CPU/RPC % | 分配 % | Δ B/RPC | P99 % | P99.9 % |
|---|---:|---:|---:|---:|---:|---:|
| tcp-Unary-1x16-c1 | +5.99 | -0.79 | +0.00 | +0.00 | -0.31 | -1.86 |
| tcp-Server-1x16-c1 | -0.41 | +1.49 | +7.77 | +112.12 | -0.03 | -4.57 |
| tcp-Server-16x16-c8 | -4.18 | +2.19 | +4.03 | +112.89 | +7.10 | +34.43 |
| tcp-Server-1000x16-c128 | +2.54 | -0.88 | -8.68 | -16594.29 | -28.18 | -28.83 |
| tcp-Server-1000x64-c128 | -3.13 | +2.72 | +19.49 | +64201.18 | -7.23 | -9.28 |
| tcp-Client-1000x16-c128 | +0.88 | +0.44 | -3.56 | -35461.85 | -1.44 | +4.54 |
| tcp-Duplex-1000x16-c128 | -0.30 | +0.56 | -2.39 | -37763.02 | +17.91 | +18.76 |
| tcp-Server-100x4096-c8 | +4.57 | -8.30 | -0.40 | -1780.75 | -1.99 | +13.28 |
| sharedmemory-Unary-1x16-c1 | -0.67 | +0.79 | +0.03 | +0.48 | +0.59 | -5.25 |
| sharedmemory-Server-1x16-c1 | -1.35 | +1.36 | +4.79 | +114.52 | +0.95 | -0.25 |
| sharedmemory-Server-16x16-c8 | -2.82 | +2.84 | +4.08 | +134.35 | +4.60 | +42.62 |
| sharedmemory-Server-1000x16-c128 | +0.55 | -0.51 | -1.07 | -1524.70 | -0.00 | -0.49 |
| sharedmemory-Server-1000x64-c128 | +0.52 | -0.31 | -0.02 | -43.73 | +3.90 | +3.60 |
| sharedmemory-Client-1000x16-c128 | -2.14 | +1.82 | -0.57 | -1526.41 | +6.81 | +7.75 |
| sharedmemory-Duplex-1000x16-c128 | -1.71 | +1.07 | -1.52 | -9733.80 | -0.17 | +1.28 |
| sharedmemory-Server-100x4096-c8 | +3.38 | -2.56 | -0.07 | -309.55 | -4.90 | +8.42 |

原始独立复算：`evidence/new/38173700315/nativeaot-sized-dto/independent-raw-pairs.json`（[原产物索引](b2-artifact-index.json)）；严格复算：`evidence/new/38173700315/nativeaot-sized-dto/strict-recomputed.json`（[原产物索引](b2-artifact-index.json)）。成功既包含业务全量 payload / item count 验证，也包含同一物理 session 前后 probe 与观测 settled 边界；这仍不等于完整生产条件下所有资源/取消/拥塞验收。

### NativeAOT A/A 原对照

下表每格给出两个 A/A 的原始变化百分比，方向固定为 `(right/left−1)×100`，不因第二次物理执行顺序倒置而翻转公式。它们是已观察到的同源变化，只有两对，不能当作严格的统计置信界。

| 场景 | 吞吐两对 % | CPU/RPC 两对 % | 分配两对 % | P99 两对 % | P99.9 两对 % |
|---|---|---|---|---|---|
| tcp-Server-1000x16-c128 | +2.649 / -3.651 | -2.837 / +3.648 | -5.001 / -0.418 | -2.296 / +8.236 | -2.073 / +9.159 |
| tcp-Server-1000x64-c128 | -5.708 / -2.302 | +0.743 / +1.689 | +14.238 / +11.134 | +122.342 / +12.849 | +128.180 / +13.432 |
| sharedmemory-Server-1000x16-c128 | -1.557 / -0.530 | +2.178 / +0.421 | +1.025 / +0.615 | +9.332 / +0.204 | +7.890 / -0.174 |
| sharedmemory-Server-1000x64-c128 | +0.315 / -0.929 | -0.342 / +0.243 | -0.198 / +0.685 | -7.794 / +22.152 | -7.666 / +25.195 |

TCP1000×16 的四对吞吐全部为正（+4.509%、+0.580%、+4.858%、+0.435%），中位数 +2.544%；CPU 中位数 −0.876%，P99 中位数 −28.176%。这是必须保留的正面观测，但未达到用户举例的 ≥5% 稳定完整 RPC 收益，A/A 吞吐本身出现 +2.649% / −3.651%；不能把一次单对、P99 改善和另一配置 CPU 节省相加。

Native eligible 最大吞吐中位数是 TCP100×4096：**+4.568%**，四个 completed-boundary 原 pair 为 **+13.815%、−0.964%、+3.712%、+5.424%**；CPU 中位数 −8.297%，P99 −1.991%，P99.9 +13.284%。其 actual local item 为 **0/RPC**、owner refill 为 **99/RPC**，所以不能用它证明 grant 批量摊销带来了 ≥5% 稳定增益。它可能包含其他整合路径差别与测量变异，本次数据没有把这些因素分离。此处数字全部来自 completed 边界，没有拿 settled 的近似数替换。

TCP1000×64 的 A/A P99 出现 +122.342% / +12.849%，P99.9 出现 +128.180% / +13.432%；对应 A/B 吞吐也有 +7.988% 的单对和 −9.092% 的单对。这个尾部变异必须和结果一起展示，不能只挑某次低 P99 或高吞吐称作生产优势。SharedMemory 两类长流的 Native 吞吐中位数仅 +0.547% / +0.516%，CPU −0.514% / −0.308%；Duplex TCP / SharedMemory 分别为 −0.301% / −1.714%。

Native 12 个 eligible cell 是 5 正 / 7 负，0 个吞吐中位数 ≥5%；TCP Unary 的 +5.993% 虽超过 5%，其全部八个 B2 计数为 0，不能作为 B2 grant 的激活收益。这个零激活控制仍保留完整数值。

## 计数的确切含义

两个 JIT 各自：18 个 eligible B 报告；activated = disposed = 116,736；firstProbeFallbacks = exactSlowAttempts = refundedItemBytes = 0；localItems = 32,735,232；ownerRefills = 448,512；recalledGrantBytes = 86,114,304。OFF / ON 另有 30 / 31 个成功 A、Unary 或 Client 控制报告，八个 B2 计数均为 0。

| Eligible workload | 每 RPC 初次授信 | Local item | Owner refill | Exact slow |
|---|---:|---:|---:|---:|
| Server 1×16 | 1 | 0 | 0 | 0 |
| Server 16×16 | 1 | 14 | 1 | 0 |
| Server / Duplex 1,000×16 | 1 | 995 | 4 | 0 |
| Server 1,000×64 | 1 | 983 | 16 | 0 |
| Server 100×4,096 | 1 | 0 | 99 | 0 |

NativeAOT 另有 48 个 eligible B 报告，activated = disposed = 270,336；firstProbeFallbacks = exactSlowAttempts = refundedItemBytes = 0；localItems = 97,878,016；ownerRefills = 1,630,208；recalledGrantBytes = 204,996,608；96 个成功控制报告八个计数均为 0。表中 per-RPC 计数在三种 runtime 均一致。

这是实际实现内部的逻辑 admission 事件计数，聚合到 lease Dispose；**不是**物理 connection-lock / CAS / queue / wakeup / writer-handoff 次数。1,000×16 的 local=995 也不能叫作“995 次完全无锁”：local path 仍有 state gate。此实验未量 ready queue、prepare budget peak、总 wire bytes 或物理 writer handoff，不能用 grant 计数给出这些未测成本的百分比分解。

计数交叉检查与 per-process / per-cell 原数：`evidence/new/38173700315/sized-independent-cross-check.json`（[原产物索引](b2-artifact-index.json)）。脚本：`evidence/b2-pilot/crosscheck_sized_rpc_reports_5cab.py`。

## 保留的前序失败与未完成验收

本次没有重跑同一配置以抹去红灯。4f 的 fixture 编译红、d472 的真实 deadline-after-admission 红、ba9 的 formatting 红以及 608 的三种 runtime 首次 harness nullable 编译红继续单独保留。5cab 只改 test-only `Validate(GrantBenchmarkPayload?)` 以符合 codec 的可空 Deserialize 契约，原 null 检查不变；production src tree 与 ba9 / 754d / 608 相同。

同 SHA 的自定义 correctness collector gate 是性能 screen 的前置门槛，但不是完整原 PR CI：它没有覆盖原独立 allocation-gate job。原报告-only PR #852 上已有单独 allocation-gate 红灯需要保留与独立解释，不能因这里 correctness gate 绿就宣称生产所有 CI 通过。

当前范围支持：**所实现 pilot 未获得生产接入所需的正面实验证据，默认资源下的真实运行失败仍是停止扩展的条件，阶段性 No-Go。** NativeAOT 已完成的成功结果不覆盖两份 JIT 红灯；没有增加窗口、发送队列、socket 参数或 watchdog，也没有削减 workload 来制造可验收结果。

## 六群体完整进程账与复算入口

独立读取本 run 六个原 `screen-plan.json` / `exits.json`，重新生成每个预声明 label，并检查 exit-0 的正式 report 真实存在且 `status=passed`。三个 sized 群体合计 241 成功 / 2 失败 / 189 NotRun；三个 byte-array 控制各 72/72 成功。因此本 run 全账为 **648 计划、457 成功、2 失败、189 NotRun**。这里仅汇总进程状态，没有汇总不同 payload / runtime 的性能或把 byte-array 当作授信激活。证据：`evidence/new/38173700315/full-firstscreen-process-ledger.json`（[原产物索引](b2-artifact-index.json)）。byte-array 的逐样本性能复核由独立审查者另报。

可复用的独立原始 reader 随仓库交付为 [`recompute_b2_sized_independent.py`](../../../eng/issue735/analysis/recompute_b2_sized_independent.py)，只用 Python 标准库，不 import 主 parser、不调用 dotnet、不硬编码 scratch 路径。输入必须是一个已解压的本版本 sized artifact 根目录，含 `screen-plan.json`、`exits.json`、`samples/`；输出另设文件。每个 runtime 单独调用：

```bash
python3 eng/issue735/analysis/recompute_b2_sized_independent.py \
  --input /absolute/extracted/nativeaot-sized-dto \
  --output /absolute/recomputed/nativeaot-sized-independent.json
```

同理对 JIT PGO OFF / ON 的各自 artifact 根运行，保留其失败与未运行位置。此 reader 固定核对 5cab / c7 的版本和本次 144-position matrix；它不是任意版本都宽松接受的通用测量工具。仓库副本与实际已执行的独立脚本逐字节相同，SHA256 `7853770335b25feb4e8f9f0ba49e70d7308b1fd297fc194ae2c63dc5501b3e64`；已有同源运行充分，无需为了“再绿一次”重复 RPC 测量或 CI。
