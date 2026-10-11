# Issue #735：D 方向锁分离的最终 No-Go 与测量方法

交付日期：2026-10-11 UTC。**本轮 D 不作为生产性能优化晋级。** 两版实际 generated RPC 筛选均未给出足够稳定的综合收益，且保留了分配回退、正反两臂的极尾及 A/A 波动。这个决定针对下面固定的 D 源码与已测负载；不能扩大成“B2、B3、混合方案已全部实测失败”。B2 的生产 pilot 尚未执行，后续若继续，应在包含独立 lease 正确性修复的新基线上另做实验。

完成的工作包括：最小生产方向控制、确定性隔离 red/green、两次各四轮 D 完整 UnitTests、两版 JIT PGO ON/OFF 与实际执行 NativeAOT 的匹配 RPC、独立分配诊断、逐样本离线复算，以及当前容器的局部 NativeAOT RPC 执行。实验收集的 `collectionSucceeded=true` 与进程 exit 0 表示收集完成；原 collector 始终保留 `productionGo=false`，未把这种成功当作发布验收。

本文件解释 D 与决策边界。全部 96 个版本×runtime×workload cell、每对数值和 A/A 见[独立数据复核](directional-independent-review.zh-CN.md)；原始 run、artifact、ZIP、plan、source 与实际 binary SHA256 见[证据索引](directional-artifact-index.json)。原 #803 的大幅退化和取消失败另见[历史审计](failures-and-evidence.zh-CN.md)，不能把它们直接归因给这个没有新调度器的 D。

## 固定基线和变更归属

本次开始工作时重新读取的 `dev` 是 [`f8ba89121c87a6410a0139c44d7db30eab877f41`](https://github.com/SunSi12138/SharpLink/commit/f8ba89121c87a6410a0139c44d7db30eab877f41)，tree 为 `007c7f8b408e7c492ca3298d1e10dab92afa8a4f`。它包含 #840 连接信用正确性修复、#841 按需路由字典，以及此前的资源清理修改。比较始终以这个实际快照为起点，不能把这些已有收益计入本次候选。后续发现的 lease-capture 缺陷是该基线已有问题，详见下文 #849；两版 D 性能二进制都尚未包含该独立修复。

D 的生产源代码首次提交为 [`a0dc552fe817780cc370329e2ef08355ddc6f82a`](https://github.com/SunSi12138/SharpLink/commit/a0dc552fe817780cc370329e2ef08355ddc6f82a)。之后测量程序的修正拥有独立提交和测量源码摘要；不会将不同测量版本混为同一组数据。

生产差异只有 `src/SharpLink.Runtime/StreamFlowController.cs`：8 行加入、4 行替换。新增 `_receiveGate`，四个接收入口使用它，`Complete` 按 `_gate` → `_receiveGate` 的固定顺序拿两个锁。没有新发送队列、grant、writer 调度器或协议路径。

| 生产输入 | SHA-256 |
|---|---|
| 基线 controller | `7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2` |
| D controller | `a9cad11d9f80d8aedce4d412ada02ade32cd8048e0cfe133ad469d029eaf658d` |

[`apply_split_gate.py`](https://github.com/SunSi12138/SharpLink/blob/a0dc552fe817780cc370329e2ef08355ddc6f82a/eng/issue735/apply_split_gate.py) 对基线 Git blob 作严格检查。[编译 stamper](https://github.com/SunSi12138/SharpLink/blob/a0dc552fe817780cc370329e2ef08355ddc6f82a/test/SharpLink.FlowArchitectureEvidence/stamp_identity.py) 在 V1/V2 实际产物中记录检查了全部 **496 个生产 `src` 文件**：除这一个 controller 之外，路径和每个工作树 blob 都必须匹配固定基线；同时核对原有序列化器/服务 fixture、包版本与构建策略、SendPump 摘要。错误模式标注和未经申明的 SendPump 修改都有失败控制，不能靠 CLI 参数把旧二进制标为新架构。

## 为什么先测这个较小的方案

A/B/C 都试图减少发送侧逐 item 的连接协调；它们分别引入 grant 生命周期、writer 准备所有权、或 fast/slow 两种执行状态。历史原型已证明这些方法可以减少某些模型中的命令数，但 #803 表明额外调度和生命周期成本会在真实 RPC 中吞掉收益。

当前 controller 将独立的 send 与 receive 状态放在同一把锁下。双向流中，发送者、接收者和消费回调会互相排队。D 用很小的生产差异检验这部分交叉争用是否值得消除。它不能回答“发送侧的连接权威能否更快”，但可以避免在尚未确认主要争用来源时直接新增调度层。

这个选择不构成对 B2 的否定。B2 必须保留为单独候选，并接受实际 generated RPC 的同源、同负载验证。

## owner、锁顺序与关闭线性化点

| 状态 | D 中的同步所有者 |
|---|---|
| send states、send state pool、连接发送信用、FIFO waiters、send lease counter | `_gate` |
| receive states、receive state pool、接收信用、pending consumed、待发跨流 WindowUpdate 数据 | `_receiveGate` |
| 首次发布 `_terminalException`、清空两侧状态 | `Complete` 同时持有两把锁 |

[`Complete`](https://github.com/SunSi12138/SharpLink/blob/a0dc552fe817780cc370329e2ef08355ddc6f82a/src/SharpLink.Runtime/StreamFlowController.cs#L507-L550) 是唯一同时拿两把锁的方法，而且永远先 send 后 receive。普通方法只拿自己所属的锁，没有 receive→send 的反向锁顺序。清理后的 waiter completion 仍在锁外执行。

关闭线性化点是持有两把锁后，首次写入 `_terminalException`。在这个点之前已经进入任一状态域的方法先完成；之后进入任一域的方法通过同一域的锁同步观察到终止状态。第二次 Complete 不能替换第一次异常。D 不依赖把异常字段改成 volatile 来补偿不完整的状态同步。

发送信用与生命周期方法没有重写。尤其 #840 的三个表达式保留：

```text
updatedStreamCredit = min(state.Credit + incomingCredit, streamWindow)
returnedCredit = updatedStreamCredit - state.Credit
updatedConnectionCredit = min(sendConnectionCredit + returnedCredit, connectionWindow)
```

连接只得到该 stream 实际恢复的额度。D 不改变 oversized 借用、部分还款、ReturnUnsentCredit、abort 后晚到更新、tombstone、pool lease、FIFO admission 的现有行为。

这是一项局部证明：它证明两个原有独立状态域可以分别串行化，不能据此宣称已有实现的所有竞态都已证明正确，也不能代替对基线本身发现的新问题进行调查。

## hot path 与空间成本

| 操作 | dev | D |
|---|---|---|
| 额度充足的 TryAcquireSendCredit | 一次 `_gate`、原字典查找、原两级扣账 | 完全相同 |
| AcceptReceived / RecordConsumed / FlushConsumed | 一次 `_gate` | 一次 `_receiveGate` |
| 无跨流 consumed update | 原 Volatile.Read 快路径 | 完全相同 |
| 有跨流 consumed update | 一次 `_gate` | 一次 `_receiveGate` |
| 连接 Complete | 一次 `_gate` | 固定顺序两把锁 |
| 每 item 新对象、队列命令、writer handoff | 原有数量 | 没有新增 |
| 每个 session 的 controller | 原 controller | 新增一个 Lock 对象及 controller 内的引用字段 |

D 没有减少锁操作的总数；它减少的是相互无关的方向之间的争用。若瓶颈是同方向锁、SendPump、序列化、Flush、网络窗口或调度，D 可能没有收益。该假设必须用真实 RPC 判定。

### 已执行的对象分配诊断：每 controller 额外 48 B

[footprint run 38165866902](https://github.com/SunSi12138/SharpLink/actions/runs/38165866902)、[artifact 11711699580](https://github.com/SunSi12138/SharpLink/actions/runs/38165866902/artifacts/11711699580) 在相同 x64 .NET 10.0.12 下，对 A/B 各执行 JIT PGO OFF、JIT PGO ON 与真实 publish 后执行的 NativeAOT。每个 phase 为 10,000 次操作、5 轮，共 390 个原始 TSV sample rows；生产差异仍只为 D。测量程序为 [`Program.cs@8e3055`](https://github.com/SunSi12138/SharpLink/blob/8e3055e4b59295dd210f530c33dfbc7a2c096610/test/SharpLink.FlowControllerFootprintEvidence/Program.cs)。

| 实测操作；三种 runtime 结果一致 | A，B/operation | D，B/operation | 差值 |
|---|---:|---:|---:|
| 直接构造一个 `System.Threading.Lock` | 40 | 40 | 0 |
| controller 构造 | 400 | 448 | **+48** |
| 首个 send state 与字典 storage | 216 | 216 | 0 |
| 首个 receive state 与字典 storage | 208 | 208 | 0 |
| 耗尽额度后创建异步 waiter | 352 | 352 | 0 |
| caller 线程 ReturnUnsent/admit waiter | 88 | 88 | 0 |
| warm 后单 state send churn | 0 | 0 | 0 |
| warm 后单 state receive churn | 0 | 0 | 0 |

因此，在该 x64 运行时的构造区间中，D−A=48 B；直接 Lock 的 40 B 加上其余 8 B 差值，与 controller 新增一个引用字段一致。这不是所有平台固定对象布局的证明，也不是 retained heap、RSS、native waiter 内存或 pool backing capacity 的测量。异步 continuation 在其他线程上的分配被该 `GC.GetAllocatedBytesForCurrentThread` 附录排除，不能把表内 88 B 称为一次唤醒的全部成本。

先前 [run 38165692785](https://github.com/SunSi12138/SharpLink/actions/runs/38165692785) 的诊断程序在 `Program.cs:201` 出现 CS8793，build exit 1，未测得分配数据；原红灯与 [artifact 11712385605](https://github.com/SunSi12138/SharpLink/actions/runs/38165692785/artifacts/11712385605) 原样保留。后续只修正诊断程序的恒真 pattern，不改生产 D、不放松断言，两个版本不混称为第一次就通过。

RPC screen 在连接构造和 warmup 后开始计量，+48 B/connection 在其主要 B/RPC 区间之外。因此下面十万 B/RPC 量级的差值不能用这个常驻对象公式解释。准确分配来源仍需要 call stack / scheduling trace；未做的测量不以推断补齐。

## 已执行的正确性控制

方向隔离测试先在未改基线执行：持有 receive 域时，send admission 应能独立前进；持有 send 域时，receive consumption 应能独立前进。基线两项按预期失败，D 通过。这是对新隔离性质的 red/green，不能把基线的单锁设计描述为协议正确性 bug。

第三项在 32 轮固定竞争中交错 send/receive churn 与 Complete，检查原始终止异常、状态计数和关闭幂等。两个独立诊断工作流中，D 各执行四轮完整 UnitTests，每轮 2,085/2,085 通过；原始输出、源代码 overlay 和二进制 SHA 均保留。

- [第一次方向控制及历史归因，run 38163848907](https://github.com/SunSi12138/SharpLink/actions/runs/38163848907)：方向 job 通过，另一个历史 job 失败。
- [第二次方向控制及历史归因，run 38164483629](https://github.com/SunSi12138/SharpLink/actions/runs/38164483629)：方向 job 通过，另一个历史 job 仍有一项失败。
- [固定 dev 基线，run 38163383883](https://github.com/SunSi12138/SharpLink/actions/runs/38163383883)：四轮 2,082 UnitTests、271 Generator、77 LoadTest tests、454 Integration tests 均通过。

不得把整个 workflow 的红灯省略，也不得把基线测试数和加入三项证据测试后的 D 测试数直接比较为相同 suite。

## 真实 RPC 筛选设计

`test/SharpLink.FlowArchitectureEvidence` 使用当前生成器、原 `IBenchmarkRpc` 契约、原 codec fixture、真实 client/server 与 TCP 或 SharedMemory。所有 worker 共用一条经过调用前后 probe 核对的物理 session，每 worker 一次只发一个 RPC。每个返回 payload 检查全量字节及总 item 数；client 上传也由服务端逐 item 检查。

这是同进程 client+server 的完整生成式 RPC。不是 transport model，也不是跨主机容量测试。

| 维度 | 第一阶段固定值 |
|---|---|
| runtime | JIT PGO OFF、JIT PGO ON、实际 publish 并执行的 NativeAOT |
| transport | TCP、SharedMemory |
| CPU affinity | 每台 runner 固定四个可用 CPU |
| 窗口/Flush | 原默认值，不重设 socket buffer 或 TCP 参数 |
| warmup | 每 worker 至少 5 秒且满足最小调用数；报告实际次数，不宣称已验证 tier 稳态 |
| A/B 顺序 | 第一轮 ABBA，第二轮 BAAB，并反转 case/transport 遍历顺序 |
| A/A | 两个 Duplex 控制、两种传输，各两对相同基线二进制 |
| 进程错误 | 首个失败即停止后续筛选，保留未执行计划及失败进程 |
| process deadline | 预先固定外部 60 秒，仅实验进程 watchdog |

第一阶段 8 个 case：Unary 1×16/c1；Server 1×16/c1；Duplex 100×16/c1；Server 与 Client 1000×16/c128；Duplex 1000×16/c32、c128；Duplex 100×4096/c8。两种 transport 下，每个 runtime 有 16 个 A/B cell，各四对、八个进程，共 128 个；A/A 另有 16 个进程。**每版为 3×144=432 个性能进程，V1/V2 共 864 个实际进程，但保持为两层独立 measurement population，不能合成每 cell 八对。** 构建、restore、元数据收集等进程不计入 144；局部本地执行和 footprint 也不加入 864。

每个 runtime 的 A/B、A/A 在同一 Actions job 内交错，三个 runtime job 或两个版本之间未证明为同一物理主机；只比较同 job 内相邻比率，不跨 job 拼绝对吞吐。它是止损筛选，覆盖范围没有冒充用户要求的最终完整矩阵。

沿用原 BenchmarkEnvironment 的测量用 request timeout 禁用，以及 SharedMemory heartbeat period=5 min / timeout=10 min；这些设置在每份报告明确披露。它们不能用于 deadline/cancel 的正确性验收，也不能把实验外部 watchdog 当作生产请求超时。

### 指标与观察边界

- 吞吐 RPC/s、logical item/s 和实际 stream payload item/s 分别报告。Duplex 的双向 payload 不能和单向 logical item 混算。
- CPU、分配与 GC 是本进程 client+server+校验的总量，不是单端的 CPU。
- 每个 RPC 保存独立开始/结束 timestamp，P50/P95/P99/P99.9 使用 nearest rank。不是并发 batch 延迟。
- 固定每 worker 的调用数，保存共同时间点的完成分布和 Jain index。少量尾部样本不能支撑稳定 P99.9 的断言。
- 版本二同时保留调用已校验完成边界及 lifecycle settlement 边界的 CPU、分配、时间。后者包含观察 WaitForIdle 的少量成本，并明确排除 raw CSV 写入、session probe、报告格式化及 transport dispose。
- 连接锁次数、原子次数、调度队列深度、准备预算峰值在这版筛选中明确为 **未插桩**。不能从 ThreadPool work item 数冒充 writer handoff 或连接命令数。

运行时报告固定 `productionScheduler=dev-SendPump`、`b3ReadyWriterEnabled=false`。NativeAOT 用编译 marker 与 `RuntimeFeature.IsDynamicCodeSupported/IsDynamicCodeCompiled` 双向核对，并散列实际执行 ELF。JIT 报告应用 DLL 与 runtime assembly 的 SHA-256。CLI 参数只能校验编译身份，不能重标记二进制。

### 两个测量版本不可混用

- [版本一，a0dc552，run 38164814650](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650)：第一版完成边界；measurement source SHA256 `12e46fb788f9e7ff11666afdcd2b55e77021abcbaffff571606452f99eb4a5dc`。
- [版本二，45bbe67，run 38165237528](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528)：预创建完成监视任务，第一时间保存 worker 原始异常，增加 settlement 成本边界，保证 cleanup 尝试完整；measurement source SHA256 `f9e722beda390c1ed982ab747023bea9af63575d1effc9136d6c1eb3d6734652`。

两版正常 RPC 工作负载和 D 生产代码相同，但 measurementSourceSha 不同。版本一仅可作限定范围的佐证，不能补齐版本二没有执行的 cell，也不能相加、拼接成更大的样本数。

## 判定方法与实际 No-Go

先检查源代码和二进制身份、实际启用模式、失败及清理，再对每个 cell 的相邻 A/B pair 计算比率。以进程作为重复单位；不能把同一个进程内数万 RPC 伪装成数万个独立 A/B 实验。

固定基线的先导 A/A 已出现明显进程间波动，部分 cell 的中位差超过 10%。因此单个 +5% 结果不足以验收。小筛选只有在真实 RPC、CPU、分配、尾部和 A/A 噪声共同给出正面信号后，才允许投入完整的混合流、信用压力、取消、compression、长流与 quantum/grant 敏感性矩阵。

正确性失败优先处理；不能继续叠加性能改动。结果接近 #803 的明显退化则终止相应方向。没有完成的实验明确列出为未验证，不能将它们描述为成功或“不可能有收益”。

### 完成边界与结算边界：不可忽略，也不能混称

V1 的 `elapsed/CPU/allocation` 以全部 RPC 校验完成为截止，后面的 WaitForIdle+probe 不在主要分母内，只另记 `settlementAndProbeMicroseconds`。V2 同时保留该 completed 边界，另取 `measured-rpcs-plus-observed-call-stream-settlement`：包含观察性 polling 与中间 counter，排除 CSV I/O、session probe、report formatting、transport dispose。两者都不是从连接建立到物理 teardown 的全部成本。[V1 源码](https://github.com/SunSi12138/SharpLink/blob/a0dc552fe817780cc370329e2ef08355ddc6f82a/test/SharpLink.FlowArchitectureEvidence/Program.cs)、[V2 源码](https://github.com/SunSi12138/SharpLink/blob/45bbe67c6920bbdac2bc5071e39d54a7e21e746c/test/SharpLink.FlowArchitectureEvidence/Program.cs) 与各原 report 保留原边界定义。

| V2 全部正常成功进程的观测范围 | completed→settled 中位数 / 最大值，µs | 增量 CPU 中位数 / 最大值，µs/RPC | 增量 allocation 中位数 / 最大值，B/RPC |
|---|---:|---:|---:|
| NativeAOT | 115.571 / 204.262 | 0.10144 / 0.45215 | 0 / 0.734375 |
| JIT PGO OFF | 144.352 / 220.552 | 0.12158 / 0.55566 | 0 / 0.453125 |
| JIT PGO ON | 164.279 / 857.465 | 0.12463 / 0.63574 | 0 / 0.296875 |

每个 V2 相邻 pair 用两种边界各算一次，吞吐百分比差的最大变化只有 **0.023224 个百分点**，CPU/RPC 为 **0.021808 个百分点**，B/RPC 为 **0.001334 个百分点**。它排除了“本次正常负载仅因少算这段结算导致数个百分点差异”的解释；不证明 cancel / fault / blocked Flush 的结算也便宜，也不授权将两个 measurement source 混池。原 pair 值与重算见[独立报告 11.2](directional-independent-review.zh-CN.md#112-settled-口径的实际成本并没有挽救性能差异)。

### V1：正信号与反证都保留

以下为四个相邻 pair 百分比的中位数，吞吐越大越好，CPU、分配和 latency 越小越好。它们只是用于解释决策的选定行，完整 48 cell 与 A/A 均在独立报告，未删掉不支持结论的数据。

| V1 cell；完成边界 | RPC/s Δ | CPU/RPC Δ | B/RPC Δ | 含义 |
|---|---:|---:|---:|---|
| NativeAOT TCP Duplex 1000×16/c128 | +1.12% | −1.81% | −0.16% | 四对吞吐 +1.69/+0.54/−0.40/+3.76%；CPU 四对减少 1.59%–3.35%，存在小信号。 |
| NativeAOT SharedMemory Duplex 1000×16/c128 | +1.77% | −1.16% | +1.23% | 吞吐幅度仍在其两次 A/A 已见 ±3.40% 量级内。 |
| NativeAOT TCP Duplex 100×4096/c8 | +9.51% | +0.65% | +1.17% | 吞吐四对 +19.34/−0.33/+25.34/−2.04%；CPU、P99 未同时改善。 |
| JIT PGO ON SharedMemory Duplex 1000×16/c32 | −6.47% | +4.88% | +20.00% | P99 +19.45%；多项成本同时退化。 |
| JIT PGO ON SharedMemory Duplex 100×4096/c8 | −3.80% | +2.37% | +1.01% | 四对吞吐都退，CPU 都增。 |

Native TCP 4 KiB 的两次大正值伴随 baseline 约 213/270 ms 的极尾。没有对应 socket/credit timeline，不能说 D 已稳定修掉一种 TCP 问题，更不能把该唯一 median ≥5% 的 Native cell 作为整体成功。V1 原始 [run](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650) 保留所有 pair 和 A/A；完整数值解释见[独立报告第 4–7 节](directional-independent-review.zh-CN.md#4-nativeaot-v1全部-16-个-cell)。

### V2：关键 Duplex 无稳定普遍改善，其他形状有得有失

下面 CPU、B/RPC 与 RPC/s 采用 **settled** 边界；latency 仍是逐 RPC validated-completion 分布。V2 全部 48 cell 的吞吐 median 均低于 +5%。判断并非把 5% 当成唯一规则，尤其不能删除清晰的小幅正面 cell。

| V2 cell | settled RPC/s Δ | settled CPU/RPC Δ | settled B/RPC Δ | 保留的解释边界 |
|---|---:|---:|---:|---|
| Native TCP Duplex 1000×16/c128 | −0.51% | −0.32% | +0.16% | V1 对应 +1.12%，没有跨版本稳定幅度。 |
| Native SharedMemory Duplex 1000×16/c128 | +0.79% | −2.20% | +5.88% | CPU 有改善，吞吐不足 1%，分配反向。 |
| JIT PGO OFF TCP Duplex 1000×16/c128 | +0.51% | −0.73% | −1.70% | P99 +34.13%、P99.9 +35.36%。 |
| JIT PGO OFF SharedMemory Duplex 1000×16/c128 | −0.80% | +0.02% | −2.08% | 中位数近零，单对可有 −12.88%。 |
| JIT PGO ON TCP Duplex 1000×16/c128 | +1.45% | −2.16% | +1.74% | 吞吐四对均正，但相同 workload A/A 已见 −3.65/−1.95%。 |
| JIT PGO ON SharedMemory Duplex 1000×16/c128 | −0.76% | −0.04% | +13.46% | allocation 差中位数 +79,057.41 B/RPC。 |
| JIT PGO ON TCP Client 1000×16/c128 | **+4.13%** | **−4.41%** | −0.97% | 四对吞吐/CPU 都有利，是最清楚的局部正信号；SharedMemory 对应吞吐 −1.66%。 |
| Native TCP Server 1000×16/c128 | **+4.02%** | −2.50% | −5.97% | 四对吞吐都正；其余运行时不能由这格代验。 |
| JIT PGO ON TCP Server 1000×16/c128 | **−4.91%** | +2.36% | **+25.25%** | 四对 allocation 全增；绝对差中位数 +124,628.46 B/RPC。 |
| JIT PGO ON TCP Duplex 1000×16/c32 | +4.10% | +0.33% | +5.59% | P99/P99.9 约 −37% 为真实现象，但不是全指标改善。 |

两项不能被中位数掩盖的 V2 负面原始样本：

- Native TCP Duplex 100×4096/c8，r1 BA 的 D 吞吐 **−24.42%**，P99.9 由 17.527 ms 增至 258.059 ms；另三对吞吐约 +1%–2%。V1 的 200 ms 极尾曾在 A，V2 又出现在 D，反驳“只在 baseline 停顿”的解释。
- JIT PGO OFF TCP Server 1000×16/c128，第一对吞吐 **−36.60%**，P99 135.017→1728.183 ms，P99.9 140.634→1730.571 ms；CPU −1.20%、B/RPC −8.82%。较少 CPU 与更长等待可以同时存在，不能把停顿都算成锁内计算变慢。

两次进程都最终 exit 0，不能归类为已证实永久信用死锁；也不能因最终成功而丢掉其尾部。此处没有 stall trace，根因保持未解释。完整来源：[V2 run](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528)、[独立报告第 11 节](directional-independent-review.zh-CN.md#11-v2-独立增补包含-observed-settlement-的计量)。

A/A 始终是独立对照。V2 c128 Duplex NativeAOT A/A 吞吐最低见 TCP −4.98%、SharedMemory −5.83%；短 TCP Duplex Native 的第二对 A/A P99.9 +124.75%。这只能描述已测 workload 的已见波动，不能给另外 12 个没有专属 A/A 的 cell 借用统一噪声阈值。c128 长流每进程 4096 RPC，P99.9 仅约 4.096 条名义尾样本；1024 RPC 的 c8 场景更少。逐 CSV 正确计算 quantile 不等于具备足够尾部统计精度。

### 已证实、合理怀疑、尚未测量

| 类型 | 判断 | 本轮证据与处理 |
|---|---|---|
| 已证实 | D 没有减少正常 send item 的连接级锁次数 | 生产 diff 只拆 receive gate；send authority、原队列和 SendPump 不变。 |
| 已证实 | D 新增 +48 B/controller 构造分配，warm 单 state churn 差为 0 | 独立 JIT/AOT footprint 的 390 行原样本；作用域限定于 caller-thread managed allocation。 |
| 已证实 | 两个版本均未达到综合生产性能晋级证据 | 全部 864 性能进程身份/CSV/退出码复算；分别保留四对、A/A、正负 outlier。 |
| 合理怀疑 | 调度/continuation/pool 时序可能解释部分过程级分配变化 | 同一静态生产差异却有较大 B/RPC 差值，+48 B 不在 RPC 区间内；没有 allocation stack，不能写成已定位。 |
| 合理怀疑 | 网络/调度等待可能参与 TCP 极尾 | 个别 CPU 下降而 wall/latency 增大；无该样本 socket/credit trace，不能移用旧 #742 stall 日志作本轮原因。 |
| 尚未测量 | 每次 Queue、TCS、锁、CAS、writer handoff 各自 ns 或 CPU 占比 | 未有操作 counters 或分解 profile；不从 ThreadPool work-item 数冒充。 |
| 尚未测量 | D 对完整生产边界、B2/B3/混合架构性能的结论 | 未做完整 credit-pressure、取消、压缩、异构流、空流、多 named stream、quantum/grant 敏感性矩阵；也没有运行 B2 生产 pilot。 |

## 基线 lease 缺陷：独立正确性工作，不计入 D 收益

在固定 `f8ba891` 的 `AcquireSendCreditAsync` 中，部分成功路径在锁内选择 state、离锁后读取 `state.Lease`。若旧 state 被 Complete、回池后以同 key 重新注册，旧 Acquire 可能读取 replacement generation 的 lease，从而错误接受旧操作。D 没有修改这些路径；因此这个 defect 是共享基线的既有缺陷，不能归咎为 D 新增，也不能被两版 864 次正常性能执行掩盖。

[run 38166410894](https://github.com/SunSi12138/SharpLink/actions/runs/38166410894) / [artifact 11712012693](https://github.com/SunSi12138/SharpLink/actions/runs/38166410894/artifacts/11712012693) 采用原 baseline+确定性 hook，原 focused 8 项中 1 项准确变红：`ReusedObjectMustRejectOriginalAcquire(True)`，exit 2，`sameKey=True sameObject=True oldLease=1 newLease=2 originalAccepted=True`。仅把 lease capture 移到原锁内的因果修复 overlay，四轮 focused 8/8、四轮 full 2090/2090 通过，均零 skip。它证明该具体 race 的因果修复，不是 B3 正确性验收。

该修复另行提出为 [PR #849](https://github.com/SunSi12138/SharpLink/pull/849)，从未改的 `f8ba891` 分出，不携带 D gate、grant 或 ready-writer。PR 的实际最终 HEAD、原 CI 和持久回归应以 PR 自身结果另行确认；这里不在仍有运行/修改时静态声称全部 gate 已绿。V1/V2 的编译身份均不包含此修复，不给它们补上“已修复”标签，也不推算修复后的性能值。

## 当前容器的实际 NativeAOT RPC 执行

原 Actions NativeAOT 筛选已经 publish 并执行 ELF。为另外确认本地可执行性，本次还实际运行了 V1 artifact 的 A/B 原二进制，执行前后 SHA256 与 `screen-plan.json` 一致。该操作与“仅看 ELF header/重新算 hash”不同，以下是真实 exit；它也不加入前述 864 进程或任何相邻 A/B 性能估计。

沿用原 11 个 CLI 参数，固定 `taskset -c 0,1,2,3`、`DOTNET_PROCESSOR_COUNT=4`、60 秒外部 watchdog；Duplex 100×16/c1，minimum warmup count=2、measured=256，原二进制的 ≥5 秒 warmup 条件仍生效。窗口、socket、codec、Flush、生产时间选项均未改变。

| 固定顺序 | 真实 exit | measured RPC 数 | 观察 |
|---|---:|---:|---|
| TCP A | 0 | 256 | 全量 payload/计数通过，同 physical session，active/pending 结算为 0。 |
| TCP D | 0 | 256 | 同上；builtForNativeAot=true，dynamic-code 两标记 false。 |
| SharedMemory A | **1** | **0** | 创建 NamedPipeServerStream 所需 socket 时 `SocketException (13): Permission denied`，尚未进入 warmup/measurement。 |
| SharedMemory D | 未执行 | 未执行 | 按首次失败后停止的固定规则，未补跑。 |

只补了下载 ELF 的 owner execute 位；没有申请额外权限、改 socket/IPC、降低负载或延长 watchdog 来绕开失败。可以确认本地 TCP generated RPC Native 已实际执行；不能声称本地 SharedMemory 通过，也不能把这次 A 环境创建失败归为 D 的信用故障。

档案 `evidence/new/38164814650/nativeaot/local-execution/` 保留完整 `*.command.json`、`*.execution.json`、stdout/stderr、report/partial CSV、`exits.json` 与 file manifest。两臂 ELF SHA256 分别为 `02b5fc07bf2a09fb3555b96e64a83d38f0f4bc7d4e34b9eaa3585d060c9d9de1` 和 `6c714749bc665330b8f36d3345b0a0470edab51bcffd32f6be0dff402532146d`，是 V1，不能换成 V2 hash。

## 原始产物、独立复算与归档异常

下列原 performance artifacts 各含 144 个执行报告、原始逐 RPC CSV、公平性样本、stdout/stderr、退出码、source manifest、编译文件与实际 ELF/DLL。完整 64 字符 SHA256 摘要、大小、源提交和提取路径都在[机器可读索引](directional-artifact-index.json)，这里给直接下载入口：

| 版本/run | NativeAOT | JIT PGO OFF | JIT PGO ON |
|---|---|---|---|
| [V1 / 38164814650](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650) | [11712895197](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650/artifacts/11712895197) | [11712571300](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650/artifacts/11712571300) | [11712845476](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650/artifacts/11712845476) |
| [V2 / 38165237528](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528) | [11712142484](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528/artifacts/11712142484) | [11712621588](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528/artifacts/11712621588) | [11712636694](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528/artifacts/11712636694) |

交付审计发现已有本地 V1 两个 JIT ZIP 不完整，均 `BadZipFile`；旧文件与 hash 原样保留并明确为无效归档。重新下载到 `d-v1-jit-pgo0-original.zip` / `d-v1-jit-pgo1-original.zip`，实际下载进程 exit 0，大小分别 35,293,477 / 35,060,967 B，SHA256 分别 `3b62a874b4534d8aa780a7cf452c4b941be068a85b77e27921c603e669ab0d74` / `e82cb8c5d4b9fb2fdbb4c74dc1377dfe37b33fe79a1f20596d46c8a66478f6b4`，均匹配 GitHub。每个新 ZIP 的 1,091 个 file entries 与原分析的解压目录逐字节核验，合计 2,182 项、0 差异；没有改任何报告、CSV 或二进制，也没有新的性能样本。完整记录 `evidence/new/d-v1-zip-recovery-comparison.json`。不能把不完整旧 ZIP 的 digest 登记成远端原 artifact digest。

[`recompute_d_screen_independent.py`](../../../eng/issue735/analysis/recompute_d_screen_independent.py) 是与主分析器独立的 Python 标准库实现。它读取一个 run 的三种 runtime，不调用 dotnet，不运行 benchmark；核对完整 144 个预定位置、exit/status、实际二进制 hash、每报告的编译/源码身份、每条 latency timestamp、nearest-rank quantile 与 completed/settled 分母，然后重算每 cell 原四对与单独 A/A。主分析器另外提供更完整的 Runtime.dll、flags 与 payload shape 校验，两者不能通过导入同一个函数形成伪独立复核。

```bash
python3 eng/issue735/analysis/recompute_d_screen_independent.py \
  --input /absolute/extracted/evidence/new/38164814650 \
  --output /absolute/recomputed/d-v1.json
python3 eng/issue735/analysis/recompute_d_screen_independent.py \
  --input /absolute/extracted/evidence/new/38165237528 \
  --output /absolute/recomputed/d-v2.json
```

交付版本实际对两版均执行成功。结果保持原数值；只有 `sourceRoot` 改为绝对路径，以及未提供 settled 数据的 V1 显式输出 null metadata。各自附 `.provenance.json` 记录当前脚本 SHA、原 input metadata SHA 和 output SHA。原大 ZIP、完整二进制和 CSV 留在 evidence archive/Actions，不塞入 Git；该目录只收小型报告、索引及重算工具。

主分析器的交付复查也已完成：六个 runtime artifacts 各自重算 exit 0，864 个报告通过更严格的计划、Runtime.dll、flags、原命令、负载、worker、timestamp/fairness 身份检查；35,752 个原数值与 paired raw arrays 逐值不变。另有 13 个分析器合成输入检查，保留旧版本处理缺臂/失败/mismatch 的反例及新版本结果；它们是离线分析器验证，不是 13 个新 RPC 性能实验或生产协议测试。原结果未覆盖，记录在档案 `evidence/new/analysis-recheck/` 的 `numeric-equivalence.json`、`guard-red-green-results.json`、`frozen-manifest.json`。

## 明确停止点

**D 在本轮到此 No-Go，不扩大生产接入，不提交为已验收性能修复。** 正面的 TCP Client/Server 小信号仍可供以后诊断参考；本轮没有强行调窗口、socket、Flush、ThreadPool 或超时来把峰值做绿。既有正确性红灯、本地 SharedMemory 执行失败、footprint 首次编译红灯和所有负面样本均保留。

B2 的生产 pilot 尚未执行；B3、混合方案也没有在当前正确性基线上完成新的生产实现验收。[框架与候选评审](frameworks-and-candidates.zh-CN.md) 提供了可迁移机制和成本模型，[历史审计](failures-and-evidence.zh-CN.md) 支持旧 #803 No-Go；这些材料不能替尚未执行的改进 B2 给出总体 No-Go。下一阶段若被继续推进，必须先满足独立正确性基线和小范围真实 RPC 的止损门槛，不能把本轮研究写成已经交付生产高性能架构。
