# B2 发送 grant 的分配成本附录

这个独立程序测量 **`GC.GetAllocatedBytesForCurrentThread()` 的阶段增量**，用于解释 B2 每连接、每代 stream 和池化复用的托管分配成本。它直接调用所编译生产版本的 `StreamFlowController`。它不是研究模型，但也不是生成式 RPC、吞吐基准、CPU 基准、完整正确性验收或 retained-heap 测量。

程序与采集器已经准备好。只有独立 B2 correctness 工作流达到门槛后，才应启动这个附录。采集器成功本身始终输出 `productionGo: false`。

## 共同基线与候选

| 身份 | 固定内容 |
|---|---|
| 原始 dev 锚点 | `f8ba89121c87a6410a0139c44d7db30eab877f41` |
| 本轮 A / common correctness control | `c7ba1768f31ce7219dc438dc6009347de6806ed5`，包含独立 lease ABA、snapshot ownership、terminal accounting 正确性修复 |
| 经审阅的 B2 生产源码 | `ba9dfedc3a769a14f03cb5c522f4921f5da655eb` 的生产 `src` 树 |
| 实际 B 二进制的提交 | 命令行明确指定的、与采集 checkout `HEAD` 相等的提交；它的全部生产源码必须与上述经审阅树相等 |

`SubjectPolicy.json` 固定共同控制完整 commit tree、独立 `src` tree、相对初始 dev 的两个正确性文件哈希，以及 B2 相对共同控制的四个文件哈希。采集器核对全部生产路径，明确要求 SendPump 不变，拒绝 D 或任何额外生产差异。后续仅添加测量文件的提交可以成为实际 B / recipe SHA；生产修改需要先更新并审阅策略文件。

这些正确性修复的效果不能算作 B2 收益。这个程序也不把以前 D 附录或旧 #735 harness 的数值作为 A/B 样本。

## 如何运行

在包含本项目与采集器的已提交 B2 研究 checkout，使用 Linux x64、仓库固定的 .NET 10 SDK 及 NativeAOT 编译工具链运行：

```bash
python3 eng/issue735/run_b2_footprint.py \
  --output /absolute/new-output-directory \
  --candidate-sha <actual-40-character-HEAD> \
  --runtime all
```

输出目录必须不存在。默认 `--baseline-sha` 已固定为 c7，不能改成初始 f8。`--runtime` 也可以明确选择 `jit-pgo0`、`jit-pgo1` 或 `nativeaot`；它改变本次明确列出的运行时范围，不把缺失运行时补成通过。

`all` 会建立 A/B 隔离 worktree，分别实际编译托管版本和 publish NativeAOT。每个运行时执行 `A → B → B → A`，共 12 个进程。每个进程做 1 个未报告的 32 主对象预热轮，然后做 5 个完整测量轮。每轮有 32 个阶段，所以完整采集应有 **1,920 条原始分配行、每运行时每阶段每臂 10 个样本**。

JIT 的 `DOTNET_TieredCompilation=1`、`DOTNET_TieredPGO=0/1`、`DOTNET_ReadyToRun=0` 与 `DOTNET_PROCESSOR_COUNT=4` 相同应用于 A/B。采集器移除相应 `COMPlus_` 覆盖，记录实际进程看到的每项设置。NativeAOT 清除 JIT tier / PGO 设置，并要求发布符号为真、`IsDynamicCodeSupported=false`、`IsDynamicCodeCompiled=false`。不把 AOT smoke 或托管执行冒充 NativeAOT。

预热只用于降低首次使用干扰，**不保证 JIT tier 或 PGO 达到稳态**。本附录不做 CPU 或耗时推断。900 秒构建、180 秒进程 watchdog 是采集器边界，不改任何生产超时、窗口或协议配置。所有正常阶段必须同步完成；若 ValueTask 需要等待，程序立即失败。

## 测量边界

每个普通阶段执行 10,000 个主对象迭代。锁阶段的一个迭代是一把 `System.Threading.Lock`；其他普通阶段的一个迭代是一个 controller 上的指定操作。池阶段用 **64 个 controller，每个拥有 128 个同时存活的 stream**；表中的一个池阶段迭代包含该 controller 的 128 个 stream 操作。原始行保留这个分母，汇总仅把 `/128` 作为明确标注的派生值。

所有数组、委托创建、诊断快照、证据计数读取、完整 GC、样本输出都在对应分配计数之外。测量体内保留通过时不分配的常量信息断言；不创建 Task、不异步切线程、不计入日志输出。程序在每个阶段前后确认 managed thread ID 相同。各轮之前执行完整 GC，生命周期检查发生在主阶段之间。

这个计数不等于对象最终保留字节：数组故意持有引用到一轮结束，状态池也会持有状态及当前代的已关闭 context。它没有测 RSS、GC 堆快照、对象存活时间、所有线程分配、NativeAOT 原生内存、锁争用 CPU、RPC/B 或每个真实 item 的完整序列化成本。已有 Lock 的 keyed 路径即使分配为零，也可能产生额外 CPU 成本，须由真实 RPC 及 CPU 证据判断。

`ItemBytes=16` 是直接传入信用控制器的 **encoded credit 字节**；本程序没有 payload、压缩或 wire DATA，不将这 16 字节描述成真实 RPC 的完整消息大小。默认 stream window 为 1 MiB、connection window 为 16 MiB、frame limit 为 4 MiB、最大 stream 数为 1,024，运行时严格核对。

## 阶段设计

完整顺序固定在 `phases.json`；采集器核对它与 `Program.Phases.cs` 的 32 次 `Measure` 调用完全一致。

| 阶段组 | 回答的问题 | A 的实际对照 |
|---|---|---|
| `lock-object-constructor`、`lock-first-enter-exit` | Lock 的分配，以及首次无竞争进入是否额外分配 | 相同 `new Lock()` / Enter / Exit |
| `controller-constructor` | controller 创建的总托管分配增量 | 相同默认参数创建真实基线 controller |
| `legacy-pristine-*` | 从未捕获 lease 的 keyed 首状态、第二 item、退款完成与池化再租成本 | 两臂都调用原 keyed API |
| `first-lease-on-pristine-pooled-state` | 已有旧状态、尚无 lazy Lock 时的首次 lease 捕获成本 | 已池化基线状态的 exact keyed reserve |
| `first-state-and-first-lease-exact-item` | 新字典条目、状态、lazy Lock 和首 lease 的合计成本 | 新基线状态的 exact keyed reserve |
| `second-item-owner-refill` | 首 item exact 之后的第一次 Q=4,096 refill | 第二次真实 keyed exact acquisition |
| `third-item-local-grant` | 从已有 Gi 消费一个 item 的分配 | 第三次真实 keyed exact acquisition |
| WindowUpdate、ReturnUnsent、Disable、后续 exact、Dispose、Complete | 信用、closed 状态和已 admission item 的不同结算边界 | 对应 keyed WU / refund / exact / Complete |
| `lease-on-reused-state-with-existing-lock` | 状态与 Lock 已池化时的新一代 context 分配 | 再次租用基线状态并 exact reserve |
| reused refill、`dispose-unused-grant`、`late-owned-item-refund-after-dispose` | Dispose 仅归还未用 Gi，之后原 helper 才退款已 reserve 的 n | keyed item reservation 保持到对应 refund |
| `legacy-*-after-leased-state` | B2 用过的状态回池后，普通 keyed 路径是否继续分配 | 真实 keyed API |
| `connection-complete-clear-pool` | 空 waiter 情况下连接终止清理的分配 | 真实基线 `Complete(exception)` |
| `pool128-*` | 完整 128 状态池的首建与再租；不会误把一个 hot slot 的 churn 当成全池成本 | 128 个真实基线 keyed state |

**A 没有 lease 的 Disable / Dispose 操作。** `disable-unused-grant`、`dispose-empty-grant`、`dispose-unused-grant` 三个阶段在 A 中明确为空操作，而其他信用转移仍在相同对应阶段发生。原始 metadata、阶段计划和汇总均标注这个区别；这些行只能说明 B2 新增生命周期操作的分配，不能说明更快。

B2 的 pool128 验证会记录第一批 128 个生产 `StateIdentity`，全部退役后再租另一请求的 128 个状态，并在计数区间外断言同一批物理对象按生产池的 LIFO 顺序复用。A 不引入反射或新的诊断生产 API。物理复用断言是生命周期证据，不是 retained-heap 大小测量。

## 防止未启用却报收益

`FLOW_EVIDENCE_B2` 只控制新测量项目中可用的诊断适配调用。B 臂直接调用真实 `TryAcquireSendCreditLease` / `SendCreditLease` API；A 调用真实 keyed API。生产代码不靠这个符号选择实现。`ModeIdentity.cs` 初始为 `UNCONFIGURED`，未经源码验证与编译期 stamping 不能运行。

每个完整 B 轮要求实际生产计数增量为：

| 计数 | 每轮期望值 |
|---|---:|
| ActivatedStreams | 46,384 |
| DisposedStreams | 46,384 |
| FirstProbeFallbacks | 0 |
| LocalItems | 10,000 |
| OwnerRefills | 20,000 |
| ExactSlowItems | 10,000 |
| RecalledGrantBytes | 81,440,000 |
| RefundedItemBytes | 1,222,144 |

其中 `3 × 10,000 + 2 × 64 × 128` 次 capture 分别来自旧池首次 lease、全新首次 lease、已有 Lock 再租，以及两批完整池。每主 controller 的 recall 为 `4,096−2×16` 的 Disable 加 `4,096−16` 的 Dispose。Refund 只统计真实 lease 所属的成功退款，不计 keyed 对照退款，也不把那次 WindowUpdate 算成 ReturnUnsent。A 的 8 项 lease 证据均为零。预热同样执行相应规模的断言，测量读取前后差值排除其计数。

这类生产计数的写入成本已包含在候选真实实现中。静态估计的 context / Lock 大小不是这里的测量结果；以实际产物为准。

## 输出与失败保留

`plan.json` 在构建前固定步骤和阶段；`exits.json` 保留每次命令、退出码、watchdog 状态、日志哈希。发生首次意外失败时其余步骤保持 `NotRun`，不重试到绿。已经写出的部分 TSV、stderr、实际失败日志都会保留。

`provenance.json`、两个 `source-*.json` 和 `compiled-identity-*.cs` 区分 recipe SHA、subject commit SHA、完整 commit tree、独立 `src` tree、所有生产文件 SHA256 和测量内容 SHA256。`measurement-source/` 保存共同程序、计划、策略与采集器；`binaries/` 保存实际运行文件，托管执行还独立核对 `SharpLink.Runtime.dll`。构建与每次运行后重新验证生产、测量、identity 和依赖文件字节没有变化。

每个样本的 `*.validated.json` 由严格解析原始 TSV 生成；检查编译 mode、实际 runtime、运行参数、默认窗口、实际启用计数、完整阶段集合、分母和原始整数分配计数。空数据、失败进程、遗漏阶段、伪造启用或 NativeAOT 标记均不能产生成功汇总。

`allocation-summary.json` 输出逐运行时逐阶段的 A/B 原始值、均值、中位数和增加的分配字节。它不把不同阶段简单相加成 B/RPC，也不把此处的分配差异转化为吞吐预测。最终 `artifact-manifest.json` 对所有保留文件逐个计算 SHA256。`result.json` 的成功仅表示这个分配附录完整采集并通过身份与样本校验。
