# Issue #735：B2 真实生成式 RPC 的测量方法、失败账与结果

本报告与 [B2 生产架构及分配附录](b2-pilot-architecture.zh-CN.md) 配套。只比较实际执行的独立测量群体，不把控制器模型、同步分配阶段、旧 B3 matched transport 或 AOT smoke 放入真实 RPC 收益表。

## 1. 身份与基线

工作开始时最新 dev 固定为 [f8ba89121c87a6410a0139c44d7db30eab877f41](https://github.com/SunSi12138/SharpLink/commit/f8ba89121c87a6410a0139c44d7db30eab877f41)，包括 #840、#841。历史 #742 的旧 baseline 没有代替它。D 的两轮独立 RPC 对照直接使用这份 dev。

B2 的真实性能测量开始前，审计、桥接接入和确定性测试发现三项需要独立处理的正确性问题；两臂共同纳入 #849 / #850 / #851，形成 [c7ba1768 correctness-only control](https://github.com/SunSi12138/SharpLink/commit/c7ba1768f31ce7219dc438dc6009347de6806ed5)。它不是移动的 dev，也不是用三个补丁的收益装饰 B2。这里的增量性能差值是 **c7 → B2**；不能称为已经测得原始 f8 → 最终组合的总收益，更不能把 D、#840、#841 或三项正确性变化的百分比相加。

B2 四个生产文件固定在 [ba9dfedc](https://github.com/SunSi12138/SharpLink/commit/ba9dfedc3a769a14f03cb5c522f4921f5da655eb)。754、608 与 [5cabd4d7](https://github.com/SunSi12138/SharpLink/commit/5cabd4d7cc5ce9aa0a87424e36954e414e0912da) 的完整 src tree 均为 `5c63c1d0eec9451115bdabd25020854deb58e901`。5cab 只修正新测试程序私有 Validate 参数的 nullable 注解，没有删减 null 拒绝或修改生产代码。

[采集器](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/eng/issue735/run_b2_screen.py) 创建两份精确 worktree，把同一测量目录放入两臂，先[核对全部生产文件、四文件许可差异、构建策略与 measurement digest](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/stamp_identity.py)，再编译。实际进程回报编入的身份，不能通过命令行把旧二进制改标签。源码、实际应用和 JIT runtime DLL/native ELF 的 SHA256 与原编译日志共同保留。

## 2. 两种 payload 群体

| 群体 | 真实合约／codec | 字节单位 | B2 路径预期 |
|---|---|---|---|
| byte-array | 原 `IBenchmarkRpc` 与 `BlitArrayCodec<byte>`，正常生成代理 | 16 B 是应用数组；编码另有 4 B 长度 | 这次 server-sized pilot 不启用，八项计数必须全 0 |
| sized-dto | 正常生成的 `IGrantBenchmarkRpc`、string + ushort DTO | 16 / 64 / 4096 B 是整个编码 item，包含字符串长度和 tag | 仅 Server / Duplex 的服务端输出可捕获 lease；Unary / Client 是零计数控制 |

DTO 不只在源码层推测可 sizing。每个进程在计数前从实际 manifest 取 codec，要求 `CanExactSize`、`TryGetEncodedSize`、sized/regular 两种序列化的完整字节一致、准确长度、反序列化后全部成员一致，记录实际 codec type 与完整 encoded item bytes 的 SHA256；后者不是 codec 实现文件的 hash。16/64/4096 B 对应 3/27/2043 UTF-16 code units；它不等于同名 byte-array 群体的应用有效载荷。相关[合约及服务](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/GrantBenchmarkContracts.cs)、[实际 preflight 与逐 item 验证](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/GrantBenchmarkClient.cs)。

每个 stream 重复一个确定性 payload，能验证丢失、多出及损坏，但不是独立的 item 重排检测。ordering 的正确性要依赖另外的生产测试，不能由重复 payload 推出。

## 3. 预声明小范围矩阵

三个独立 runtime population 为 JIT PGO OFF、JIT PGO ON、实际 publish/execute NativeAOT。每个 population 同机匹配 TCP 和 SharedMemory；每个 cell 依次保留 ABBA、BAAB，共四个 matched pair。不同 runner 的绝对吞吐不直接比较。

| payload | shape | items/RPC | 编码或应用 B/item | 并发 | 每 worker 测量 RPC 数 |
|---|---|---:|---:|---:|---:|
| sized-dto | Unary | 1 | 16 encoded | 1 | 32768 |
| sized-dto | Server | 1 | 16 encoded | 1 | 16384 |
| sized-dto | Server | 16 | 16 encoded | 8 | 512 |
| sized-dto | Server | 1000 | 16 encoded | 128 | 32 |
| sized-dto | Server | 1000 | 64 encoded | 128 | 32 |
| sized-dto | Client | 1000 | 16 encoded | 128 | 32 |
| sized-dto | Duplex | 1000 | 16 encoded | 128 | 32 |
| sized-dto | Server | 100 | 4096 encoded | 8 | 128 |
| byte-array | Unary | 1 | 16 application | 1 | 32768 |
| byte-array | Server / Client / Duplex | 1000 | 16 application | 128 | 32 |

sized-dto 的 Server 1000×16/c128 与 1000×64/c128 另有 A/A；byte-array 的 Server 1000×16/c128 另有 A/A。每个 runtime 规划 DTO 144、byte-array 72 个进程，三 runtime 共 **648 个计划位置**。是否执行、退出是否成功、是否实际启用由结果逐一填充；计划数不冒充完成数。

grant Q 固定 4096 B。该小范围先测试吞吐、CPU、分配与生命周期开销能否产生可重复正面证据，未遍历 Q 或 B3 quantum。旧 B3 q=1/4/8/16/32 的 matched-transport 敏感性是另一份历史证据；不能补上新 B2 的未测 Q 或新 B3 的生产 RPC gate。

## 4. 测量边界及控制

[程序](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs) 为每 worker 创建一个持续任务，每次只有一个 outstanding RPC，共用一条物理连接。每 worker 预热同时满足最小调用数和 5 s；记录实际预热，不声称由此证明 JIT tier 已完全稳定。任务、闭包、latency 数组和协调对象在测量前创建。四核 affinity 与 ThreadPool minimum `max(32, processorCount×4)` 在同一 A/B 中完全相同。

生产默认窗口、socket 参数、SendPump queue、保留预算与 Flush 不变。原 benchmark 的禁用 request timeout、SharedMemory heartbeat 5/10 min 策略在两臂相同并显式披露，不能用这个屏幕替代真实 Deadline/取消验收。外部每进程 60 s watchdog 不放宽；超时、worker 原异常、stderr、已经生成的 partial CSV 与 NotRun 保留，不重试到绿。硬 kill 可能发生在 CSV 写入前，此时保留 started/stdout/stderr 等实际文件并标明缺失；不能由没有 CSV 推断执行了零个 RPC。

每个 RPC 在开始与全部 payload 验证完成时独立取 timestamp。分母与输出分开：

- RPC/s 是完整调用吞吐；item/s 是逻辑 workload item。Duplex 每 logical item 有两个 StreamData payload，另报该分母。
- CPU/RPC 与 B/RPC 是**整个客户端 + 服务端 + 完整验证**进程成本，包括共同起跑和尾部协调。CPU/item、B/item 使用明确逻辑分母。
- completed 边界结束于全部 RPC 验证完成；settled 边界继续包含 `WaitForIdleAsync` 观察到 calls/streams 为 0 的时间、其 observer 开销和中间计数读取。不能把差值当作纯生产 cleanup 时间。
- 完整 raw CSV 重算 nearest-rank P50/P95/P99/P99.9/max；报告中的尾部规模 N/1000 只是名义数量，可非整数，没有做相关性或统计有效样本量修正，有限样本不证明 P99.9 安全。
- GC collection/pause、ThreadPool completed work、逐 worker latency/span 与 10/25/50/75/90/100% 时间点的 Jain 分布保留。若某时点所有 worker 尚无完成，记 N/A；最终每 worker 固定次数使 100% Jain=1。早期 RPC 完成分布可用于诊断，但不能替代 stream-credit/FIFO 公平性的证明。
- raw CSV/result 文件 I/O、额外 session probe 与 transport Dispose 不进入成本区间；计数读取与 settled observer 自身仍在上述测量边界内；成功结果只在实际 Dispose 完成后发布。两个 probe 必须返回同一个物理 session ID。

settled 只证明报告所检查的 calls/streams 指标为 0，**没有证明全部 late WU、SendPump、准备预算或信用都已空闲**。有关有限观察的边界，不能改写成资源守恒的证明。

## 5. 激活与操作计数

八项 production counter 记录 warmup 后到 settled 的差：Activated、Disposed、FirstProbeFallbacks、LocalItems、OwnerRefills、ExactSlowAttempts、RecalledGrantBytes、RefundedItemBytes。[bridge](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/GrantEvidenceBridge.cs) 与[严格 parser](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/eng/issue735/recompute_b2_screen.py) 共同拒绝误标：

1. eligible B 的每个成功 Server/Duplex RPC 必须对应 activation 或首 probe fallback，Activated=Disposed。
2. 小 item 长流必须有实际 LocalItems；同时报告并核对 OwnerRefills。后者不是 parser 的另一个独立大于零分支。4 KiB 每 item 可能耗尽整个 Q，不能要求同样 local hit。
3. A、byte-array、Unary、Client 八项必须全 0；“编译了 B2”不能代替执行了 B2。
4. 实际 NativeAOT 必须是所保存 ELF，编译 marker 与动态代码 supported/compiled=false 相符，保留 publish 日志和直接执行命令。

每个 local item 的计数在原 state gate 内更新，Dispose 汇总到全局；观测成本计入 B。这些计数不是所有全局锁或机器原子指令的计数。connection WU、生命周期、SendPump、pool 内的锁、CAS、queue command、实际唤醒、writer handoff、prepared-byte 峰值和 ready 深度**没有在此全量埋点**；不能从 LocalItems、ThreadPool 或 payload bytes 捏造那些数据。

无压力、同样大小且 grant 未被回收时，Q/n 可描述 refill 的理论摊销；存在 WU、公平等待、取消及 pressure scan 时必须用实际计数。减少信用 owner entry 不自动减少 SendPump 数据队列和 DATA frame 的数量。

## 6. 失败与准入账

[独立工作流](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/.github/workflows/flow-b2-rpc-screen.yml) 先对实际测量 SHA 跑 25 项预声明 gate，再允许 RPC 和 footprint job。25 项包含 focused41、四轮 full2123、generator271、load77、integration454、原工程检查和指定 mutation red/green；**不等于原 PR 所有独立 job**，不包括另一个 allocation-gate job、Package Smoke 和 CodeQL 的完整替代。

| 提交／run | 实际结果 | 证据解释 |
|---|---|---|
| 4f564d31 / [38170726573](https://github.com/SunSi12138/SharpLink/actions/runs/38170726573) | fixture int→uint 编译错误；测试 NotRun | 构建失败，不是运行时竞态 |
| d472eee7 / [38171089212](https://github.com/SunSi12138/SharpLink/actions/runs/38171089212) | focused40/41；deadline 已 admission 的 64 B 未结清 | 真正生产桥接 red，后续性能停止 |
| ba9dfedc / [38171674863](https://github.com/SunSi12138/SharpLink/actions/runs/38171674863) | 原同一 deadline 通过；全部 runtime suite 通过，whitespace gate 红 | 整轮仍为 failure；剩余工程/mutant NotRun |
| 754d723c / [38172137769](https://github.com/SunSi12138/SharpLink/actions/runs/38172137769) | 25/25 gate 通过；指定 mutant 仍精确 red | 仅格式修正，没有改生产字节 |
| 60828816 / [38172711056](https://github.com/SunSi12138/SharpLink/actions/runs/38172711056) | 25 gate、footprint 成功；六个 RPC job 全在 build-A CS8604 失败 | **0 RPC / 648 NotRun**；没有 DTO preflight，不是性能回退 |
| 5cabd4d7 / [38173700315](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315) | 25 gate、footprint、三 byte-array 与 NativeAOT DTO job 成功；两个 JIT DTO job 因发送队列容量拒绝失败 | 648 计划位置：457 成功、2 Failed、189 NotRun；整轮仍为 failure |

608 的六份原始小 ZIP 共 120 个条目，均与 GitHub SHA256 及解压副本对应；三 byte-array / 三 sized-dto 的每份 18 项 measurement input 分别与 608 Git 对象匹配。没有上游 internal artifact manifest 的失败包，本地 inventory 明确标为本地清单。新参数类型匹配 `IRpcCodec<T>.Deserialize` 的 `T?` 返回；方法体原来的 null 拒绝没有变化。原 608 红灯永远不被后续新 run 覆盖。

## 7. 已完成的真实 RPC 结果

**本轮结论：当前 B2 pilot No-Go，停止扩大接入。** 实际减少信用 owner entry 的效果没有转化成跨运行时、传输和完整 RPC 的稳定收益；两个 JIT population 又被真实资源拒绝提前终止。NativeAOT 成功执行不能覆盖它们的 Failed / NotRun。这里不是所有未来 B2/B3/混合架构不可能成功的证明。

测量目录的统一 digest 是 `db0d2bce291742a67a8635516d0f2ff16b1faa35877ced6aa8667573f42a3c87`；只用于本轮 5cab，不能复用到 608。详细原值、所有正负 pair、A/A、逐 RPC quantile 和 source/binary 核对见 [sized DTO 独立复核](b2-sized-dto-independent-review.zh-CN.md)、[byte-array 控制独立复核](b2-byte-array-control-review.zh-CN.md) 与 [B2 artifact 索引](b2-artifact-index.json)。

### 7.1 完整执行账

| population | 计划 | 成功 | Failed | NotRun | 实际含义 |
|---|---:|---:|---:|---:|---|
| JIT PGO OFF / byte-array | 72 | 72 | 0 | 0 | 八项 B2 counter 全 0；仅控制 |
| JIT PGO ON / byte-array | 72 | 72 | 0 | 0 | 八项 B2 counter 全 0；仅控制 |
| NativeAOT / byte-array | 72 | 72 | 0 | 0 | 实际 ELF 执行；八项 counter 全 0 |
| JIT PGO OFF / sized DTO | 144 | 48 | 1 | 95 | 基线 A 在预热 SharedMemory Server1000×64/c128 时容量拒绝 |
| JIT PGO ON / sized DTO | 144 | 49 | 1 | 94 | B2 B 在相同 workload 的预热时容量拒绝 |
| NativeAOT / sized DTO | 144 | 144 | 0 | 0 | 实际 ELF 执行；完整四对与 A/A |
| **合计** | **648** | **457** | **2** | **189** | **整轮失败；不重试、不补跑缺失位置** |

两个 JIT DTO 各只有 12 个 cell 完成两对，其中 9 个 cell 属于 B2 可启用流；预声明 A/A 全部 NotRun。PGO ON 另保留一个已成功、但下一臂失败而无法配对的 SharedMemory 1000×64 A。NativeAOT DTO 则完整完成 16 个 cell 的四对和 8 个 A/A pair。三个 byte-array 各 8 个 cell 的四对及 4 个 A/A pair 全部完成；不与 DTO 混合。

独立 reader 从原 stdout/JSON、CSV 和二进制重新计算：sized DTO 成功报告共 2,224,128 条逐 RPC 样本，byte-array 共 2,260,992 条。失败位置的正式 CSV 是只有表头的真实文件，不能当成有效的零吞吐观测。两组总样本数是文件核对计数，不是独立、同分布样本量或性能置信度。

### 7.2 代表长流的 matched-pair 中位数

下表吞吐、CPU 与分配明确采用 **completed 边界**；`Δ=(B/A−1)×100%`，吞吐正数较好，CPU、分配和延迟负数较好。每一行的 N 只计算该 cell 的实际完整 pair。**JIT 的 N=2 是被失败截断的部分数据，没有本 population 的 A/A，不能称稳定验收。** 全部 cell 和原 pair 已另表交付，没有只保留这些代表场景。

| runtime | transport / workload | N | RPC/s Δ | CPU/RPC Δ | B/RPC Δ | P99 Δ | P99.9 Δ |
|---|---|---:|---:|---:|---:|---:|---:|
| JIT PGO OFF | TCP / Server-1000x16-c128 | 2 | -4.31% | +2.08% | +10.47% | +47.61% | +45.22% |
| JIT PGO OFF | TCP / Server-1000x64-c128 | 2 | +0.22% | -0.52% | -2.82% | -2.88% | -5.24% |
| JIT PGO OFF | SHM / Server-1000x16-c128 | 2 | -0.12% | -0.37% | -0.95% | +44.27% | +45.41% |
| JIT PGO OFF | TCP / Duplex-1000x16-c128 | 2 | -1.76% | +2.07% | +0.83% | -26.48% | -20.38% |
| JIT PGO ON | TCP / Server-1000x16-c128 | 2 | -4.16% | -0.71% | +10.99% | +66.03% | +75.52% |
| JIT PGO ON | TCP / Server-1000x64-c128 | 2 | -1.28% | +0.80% | -2.69% | +5.79% | +5.59% |
| JIT PGO ON | SHM / Server-1000x16-c128 | 2 | -2.55% | +2.17% | -0.14% | +15.77% | +16.20% |
| JIT PGO ON | TCP / Duplex-1000x16-c128 | 2 | -1.13% | +0.27% | -2.36% | +18.55% | +20.08% |
| NativeAOT | TCP / Server-1000x16-c128 | 4 | +2.54% | -0.88% | -8.68% | -28.18% | -28.83% |
| NativeAOT | TCP / Server-1000x64-c128 | 4 | -3.13% | +2.72% | +19.49% | -7.23% | -9.28% |
| NativeAOT | SHM / Server-1000x16-c128 | 4 | +0.55% | -0.51% | -1.07% | -0.00% | -0.49% |
| NativeAOT | TCP / Duplex-1000x16-c128 | 4 | -0.30% | +0.56% | -2.39% | +17.91% | +18.76% |
| NativeAOT | SHM / Server-1000x64-c128 | 4 | +0.52% | -0.31% | -0.02% | +3.90% | +3.60% |
| NativeAOT | SHM / Duplex-1000x16-c128 | 4 | -1.71% | +1.07% | -1.52% | -0.17% | +1.28% |
| NativeAOT | TCP / Server-100x4096-c8 | 4 | +4.57% | -8.30% | -0.40% | -1.99% | +13.28% |

Native TCP 1000×16 的四对吞吐均为正：`+4.509,+0.580,+4.858,+0.435%`，CPU 四对也均下降；这是应保留的正信号。其吞吐中位数 +2.544%、CPU 中位数 −0.876%、分配中位数 −8.678%，但分配第四对为 +1.355%，P99 原四对为 `−55.726,+4.188,−54.980,−1.373%`，不能把中位数改写为全部尾部改善。该 cell 的 A/A 吞吐 `+2.649,−3.651%`，也不能用两对 A/A 简单相减取得“校正收益”或建立显著性。

最大的 Native eligible 中位数是 TCP 100×4096/c8 的 +4.568%，原四对 `+13.815,−0.964,+3.712,+5.424%`，CPU 中位数 −8.297%。这个场景每 RPC 实际 **LocalItems=0、OwnerRefills=99、首 exact=1**：它可能反映其他已接入成本差异或运行波动，不能归因于多 item grant 的摊销，也不能挑 +13.815% 当整场景成功。Native TCP 1000×64 的 A/A 吞吐为 `−5.71,−2.30%`，P99 为 `+122.34,+12.85%`；控制中的尾部变化同样保留。

两个 JIT 的 9 个 eligible cell 各为 2 正 / 7 负；四个表面正 cell 的原两对全部一正一负。Native 12 个 eligible cell 和两个 JIT 各 9 个已观察 cell，**30 个 cell 都没有吞吐中位数达到 5%**。这不是把“5%”设为唯一机械门槛：同一评审同时考虑 CPU、分配、尾延迟、资源失败、观测噪声、覆盖缺失，以及新增对象／生命周期／公平性成本。没有充分收益支持扩大风险范围。

### 7.3 确实减少协调，没有默默退回旧路径

在成功的 1000×16 B eligible B2 RPC 中，每 RPC 实际 `LocalItems=995、OwnerRefills=4、Activated=Disposed=1、fallback=0`；1000×64 B 为 `LocalItems=983、OwnerRefills=16`。加上一次 first exact，分别只有 5 / 17 次信用 admission 需要进入 owner，而原逐 item exact 路径为 1000 次；这里不包括 WU、退款、生命周期或 SendPump 的协调。每 item 仍要拿 state gate，并支付原 DATA 的预算、队列、writer 和编码等成本。

因此可作出的结论是：**优化在目标层工作了，但信用 admission 的大幅摊销不足以解释完整 RPC 的主导成本。** 不能倒推每次 owner lock 多少 ns，也不能把整体吞吐差值分摊给 Queue、Task、TCS、callback、Flush 或压缩。需要独立归因实验才能量化那些份额；本轮没有为救性能继续叠加新的 writer 队列、压缩或背压重构。

全部成功 eligible B 报告的 activation/Dispose 配平，证明 lease 的观测结束，不证明 prepared buffer、全部跨代引用或晚到 WindowUpdate 已全面结清。所有 byte-array、A、Unary/Client 控制的报告八项计数为零；A 二进制没有 B2 API，共同 adapter 返回默认零，B 才读取真实 B2 计数。这不是原版所有锁／队列的零操作观测。它们的原始正负性能只说明新字段、fallback/legacy 分支与运行控制，不是 B2 grant 已启用的收益。

## 8. 容量拒绝与最终止损依据

两次红灯的准确解释见 [发送队列容量独立归因](b2-queue-capacity-attribution.zh-CN.md)。原异常为 `Session send queue exceeded its 8388608-byte limit (send_queue_capacity)`，一次 A、一次 B，均在 worker 预热时发生。发送队列 normal 上限为 8,388,608−16,384=8,372,224 B；实际 64 B 编码 item 的完整 DATA frame 是 81 B。条件模型中一波 128×1000 item 可需要 8,192,000 B 信用而产生 10,368,000 B DATA frame，所以信用守恒不保证发送队列后于信用形成背压；**不是实测队列峰值**。

SendPump 的 TryReserve 在 CAS 加入 Q 前检查上限，Full 不为该帧预留 Q。因此不能把异常字面解释成已经突破 hard cap。基线也拒绝，排除了“只有 B2 才会发生”的说法；不同 runtime 的两次失败不是匹配的故障率比较。现场缺少失败后信用、prepared budget 和 owner 的完整结清快照，也不能宣称已经证明没有泄漏或死锁。harness 首错后主动 teardown，后续 stopping/cancellation 不是 128 个独立容量故障，也不证明产品因一次逻辑流取消无故关闭整条连接。

继续让已授信的 frame 异步等待 SendPump 容量，需要另行证明取消、未发布退款、writer 接管和终态等待的所有权。这是独立生产改动；在当前屏幕没有稳定收益的情况下，本轮不为通过负载而改变队列、窗口、Flush、超时或调用量，也不扩大该重构。

最终 No-Go 的范围如下：

| 对象 | 本轮决定与证据边界 |
|---|---|
| 当前 B2 server-sized pilot | **No-Go**；真实局部摊销已实现，端到端收益不足、JIT profile 不完整且有真实资源拒绝；保持研究分支，不建议生产合并 |
| D send/receive gate split | **No-Go**；独立两轮 864 个真实 RPC 进程，最终 V2 全 48 中位数低于 5%，CPU/分配/尾部未形成一致优势；不把 B2 数据混入 |
| 旧 #803 B3 | **No-Go / 不恢复**；旧真实 RPC 大幅退化与可重现取消所有权失败已有独立证据 |
| 新 B3 / 混合 writer | 设计评审完成，未做完整新生产接入；按止损点不晋级，不宣称已经穷尽或实测证伪每一种实现 |
| #849 / #850 / #851 | 小范围 correctness-only PR 独立保留；原 red 与最新 green 分开，收益不计入 B2。创建时均 Draft，交付时 #850 已由仓库转为 Ready for review，三个 head 未变 |

本轮没有完成请求中的全矩阵：新 B2 的 c32、64 item、超过1000 item的长时间稳态、Q/队列敏感性、指定周期 WU/耗尽/拥塞的匹配性能、混合/named/churn、压缩、blocked Flush 清理和完整资源峰值仍缺实验；全部锁/CAS/queue/wake/handoff 的成本占比也未测。静态检查另发现基线继承的组合 HOL 公平性缺口，见架构附录；并非已执行的新 red/green。上述缺口都阻止 Go，不能拿历史模型、25 个 staged gate、AOT footprint 或已有其他 test green 补齐。

这次交付是**证据支持的阶段性生产 No-Go 和可复算研究结果**，不是一套达到所有生产验收门槛的 B3，也不是通过无限试错证明某个预设方案。
