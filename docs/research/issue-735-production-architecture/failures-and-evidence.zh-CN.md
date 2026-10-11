# Issue #735：生产架构历史失败归因与原始证据审计

审计截止：2026-10-11 UTC。本报告是当前生产架构评审的历史证据附件，涵盖 Phase A、B0/B1/B2/B3、关闭的 #800/#803，以及本次固定历史版本的确定性归因实验；不把诊断用 green overlay 当作生产实现。

## 1. 研究审计结果与最重要的三个失败根因

**旧 #803 为 No-Go。**原始真实生成式 RPC 的 416 条 JIT PGO ON 记录中，26 个 transport/shape/concurrency 组合有 25 个吞吐中位数为负。c1、100×16 B Duplex 相对当时 dev，四轮配对中位数为 TCP **−31.18%**、SharedMemory **−32.82%**；同一 TCP 场景 CPU/RPC **+53.81%**、分配 **+11,491.46 B/RPC**。这些数据已经从原始 JSON 独立重算，不是转述评论。[原始性能 run](https://github.com/SunSi12138/SharpLink/actions/runs/38140928821)、[artifact 11703138985](https://github.com/SunSi12138/SharpLink/actions/runs/38140928821/artifacts/11703138985)

前三个必须先解决的问题是：

1. **研究中省掉的 producer 信用 gate，被生产中的动态状态、准备预算、发布和完成责任重新包围。**#803 已解析、无压力普通 DATA 仍有 4 次 producer stream gate、3 次 writer stream gate，另有至少 2 次准备预算共享 RMW，以及原有 SendPump 的至少 2 次预算 RMW；q1 每帧轮转。源码能确认操作存在，RPC 测量能确认总成本回退，但没有 profile 可以把全部回退逐纳秒归到某一个 Queue、TCS 或锁。B2 的较早 publication model 已显示：即使 owner commands/item 不变，加入 writer ownership 也会增加整体模型时间，详见第 3、6 节。[发布代码](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.Publication.cs)、[调度与完成代码](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.Scheduling.cs)
2. **terminal 没有沿同一个不可变生命周期传递异常。**远端 abort 已选择原异常后，EOF 使用 default lease 重新按 key 调用 `Open`；旧 `Open` 将已有 error 改成 generic `The stream is closed.`。历史八轮完整测试两次出现此错误；本次固定 `8ac4c849` 的 red/green 在真实 session 路径中精确复现并锁定了这条可达因果链。实验仅覆盖退休完成前的窗口，不声称全部 terminal/generation 竞态已经解决。[terminal 入口](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/RpcSession.WriterOwnedStreaming.cs#L69-L78)、[旧 Open](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.cs#L109-L123)、[本次 v2 运行](https://github.com/SunSi12138/SharpLink/actions/runs/38164483629)
3. **取消 token 已置位不等于 prepared 清理已经完成；客户端出口依赖一个仍可被撤销的回调。**旧 client 的 `finally` 只 Dispose cancellation registration。较晚注册的回调阻住取消线程时，producer 可以先完成并撤销尚未启动的 writer cleanup callback。本次 red 在调用结束时以及解除回调屏障后均保留 42 B；在 `finally` 明确结算 immutable lease 的归因 green 则为 0，并保留 producer 原异常身份。[ClientConnection](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Client/ClientConnection.cs#L351-L445)、[取消注册](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/RpcSession.WriterOwnedCancellation.cs#L5-L17)、[原始压力 artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38142405753/artifacts/11703033512)

本次起始最新 dev 已固定为 [`f8ba89121c87a6410a0139c44d7db30eab877f41`](https://github.com/SunSi12138/SharpLink/commit/f8ba89121c87a6410a0139c44d7db30eab877f41)，包括连接信用修复 [#840 / `45e5549587c9c8690f245a95a9edcc7c14634718`](https://github.com/SunSi12138/SharpLink/commit/45e5549587c9c8690f245a95a9edcc7c14634718) 和按需路由字典 [#841](https://github.com/SunSi12138/SharpLink/pull/841)。历史 #803 的旧 dev 是 `0fe26024b114bb6e78411a9b86276086c045d03d`；Phase A 的旧 baseline 又是另一版本。**任何这些历史增益都不能算作相对当前 dev 的新增收益；旧 #803 No-Go 也不能外推为所有重新设计的 B2/B3/混合方案均不可行。**

## 2. 审计范围、版本与证据层级

### 2.1 历史讨论与代码身份

已读取并保存 #800/#803 的完整 PR 元数据、conversation comments、review submissions、inline review threads、提交及 changed files；#800 有 1 条 conversation comment，#803 有 5 条，二者 review submissions 和 inline threads 为空。两个 PR 分别 9、14 个历史提交的完整 patch 均已保存，未用最终文件代替演化审查。

| 对象 | 固定身份 | 解释 |
|---|---|---|
| [#800](https://github.com/SunSi12138/SharpLink/pull/800) | base `e834d3c28c87ad496989af925515cf21babd308d`；head `453394316a05c0402b88b68e9a38e7546eb365f2` | 关闭、未合并、Draft；尚未接通正常生成式 RPC，因此没有另一份可冒充真实 RPC 的生产性能结论。 |
| [#803](https://github.com/SunSi12138/SharpLink/pull/803) | base 同上；final head `8ac4c84938d193c9a9659e9facd71f1fff82968a` | 关闭、未合并、Draft；真实生产路径在多次提交中逐步加入，见第 3.6 节。 |
| #803 性能主体 | `39f27f08d9704e19b5ec7523f4a9f7494fe828cc` | 对照旧 dev `0fe26024b114bb6e78411a9b86276086c045d03d` 和同 candidate 的 legacy 模式。 |
| #803 压力及本次归因主体 | `8ac4c84938d193c9a9659e9facd71f1fff82968a` | 相对 39f 只补充断言上下文与压力 workflow；不把不同 SHA 的全部验证混为一个检查点。 |
| #742 B3 matched transport | head `9628bbc6782dc8a9696e250caa5a984c53d69a43`；Actions merge checkout `aacc9bd54975717a211e2f400289ab3857c85677` | 对 checkout 应用 artifact 原 patch，以独立 Git index 重建出一致的 measured tree `1c90f0e0eea224204feb0680c0bb6742b1380f1b`。该 tree 是临时树，不伪造对应 GitHub commit 链接。 |
| Phase B full-controller/model | head `141608ce1c256c0e92d0c5611491cb24af419cda`；实际 merge checkout `7d01a3f6ef3e1efbbf03f9b4ca33fbedd4769977` | 已 fetch 实际 checkout，核对研究相关文件与 review head 无差异。 |

讨论入口：[Phase B 跨框架评论](https://github.com/SunSi12138/SharpLink/issues/735#issuecomment-5796560066)、[active-key 范围说明](https://github.com/SunSi12138/SharpLink/issues/735#issuecomment-5825666257)、[#803 首次取消失败](https://github.com/SunSi12138/SharpLink/pull/803#issuecomment-6109356765)、[真实 RPC No-Go 评论](https://github.com/SunSi12138/SharpLink/pull/803#issuecomment-6109441211)、[关闭与八轮压力结论](https://github.com/SunSi12138/SharpLink/pull/803#issuecomment-6111294915)。本报告所有实测结论以对应原始 artifact 和日志为依据，评论只用于追踪。

### 2.2 不混用的五个层次

| 层次 | 本次审阅的对应证据 | 不能由其推出的结论 |
|---|---|---|
| 研究模型 / microkernel | Phase A keyed/resolved、B0 frozen controller、B1/B2 grant、publication | 无真实 peer/codec/生命周期时，不能推出完整 RPC 吞吐与生产正确性。 |
| 相同发送路径的 matched transport | A-ready/B3-ready，共用 bounded ring、SendPump、transport、接收和 Flush 路径 | 仍是固定 stream slots 和模式化 bytes，不能冒充生成式 RPC。 |
| 真实生成式 RPC JIT | Phase A 84 条 c1；#803 416 条；另有真实 StreamLoadTest | 不能补齐未测的 PGO OFF、NativeAOT、默认窗口或尾延迟。 |
| 已编译、实际执行 NativeAOT | Phase A microkernel、Phase B model、B3 matched transport | 执行是真实的，但证据范围仍限于各自模型；没有由此产生 #803 完整 RPC AOT 对照。 |
| NativeAOT smoke | #803 package smoke CI | 只能证明 smoke 覆盖路径可发布/运行，不能抵消完整测试失败或证明性能收益。 |

### 2.3 原始 artifact 注册表

下载后逐份检查 ZIP 大小与 SHA256，均与 GitHub artifact metadata 的 digest 相符。下表路径相对于交付证据档案的 `evidence/historical/`，两次早期归因运行放在 `evidence/new/`。ZIP 不加入 Git。

| Run / artifact | 内容 | ZIP bytes | 解压相对路径 |
|---|---|---:|---|
| [35862800731 / 10751542617](https://github.com/SunSi12138/SharpLink/actions/runs/35862800731/artifacts/10751542617) | Phase A，1,624 microkernel rows、84 RPC records、24 c8 records/96 results、原始 native ELF | 5,936,342 | `phase_a/` |
| [35893121186 / 10765897507](https://github.com/SunSi12138/SharpLink/actions/runs/35893121186/artifacts/10765897507) | Phase B，10 份 JSON、624 model rows | 24,479 | `b2_model/` |
| [36018718001 / 10816087303](https://github.com/SunSi12138/SharpLink/actions/runs/36018718001/artifacts/10816087303) | matched transport JIT PGO 0/1，48 进程、768 samples | 678,441 | `b3_jit/` |
| [36018718001 / 10815623160](https://github.com/SunSi12138/SharpLink/actions/runs/36018718001/artifacts/10815623160) | matched transport 实际 NativeAOT，8 进程、128 samples | 117,346 | `b3_native/` |
| [36019812964 / 10815718972](https://github.com/SunSi12138/SharpLink/actions/runs/36019812964/artifacts/10815718972) | 失败 TCP 慢速前进诊断，非有效 timing | 18,191 | `b3_stall/` |
| [38140928821 / 11703138985](https://github.com/SunSi12138/SharpLink/actions/runs/38140928821/artifacts/11703138985) | #803 真实 RPC JIT PGO ON，416 records | 800,746 | `performance-39f27f08/` |
| [38142405753 / 11703033512](https://github.com/SunSi12138/SharpLink/actions/runs/38142405753/artifacts/11703033512) | #803 八轮完整 UnitTests，3 轮失败 | 5,354 | `stress-8ac4c849/` |
| [38163848907 / 11711417040](https://github.com/SunSi12138/SharpLink/actions/runs/38163848907/artifacts/11711417040) | 本次 v1 red/green，完整 green 3 失败 | 15,579 | `../new/38163848907/historical/` |
| [38164483629 / 11710959866](https://github.com/SunSi12138/SharpLink/actions/runs/38164483629/artifacts/11710959866) | 本次 v2 red/green，完整 green 1 失败 | 15,517 | `../new/38164483629/historical/` |
| [38165035402 / 11712415123](https://github.com/SunSi12138/SharpLink/actions/runs/38165035402/artifacts/11712415123) | 本次固定 H/G 八轮控制，原 H 1 失败 | 9,771,728 | `control_matrix_9910c41/` |

| Artifact | 验证的完整 SHA256 |
|---|---|
| 10751542617 | `015fe412aebcff102f3b6c9c31c25c078b520429c06cc1e79ef0674ee594cb93` |
| 10765897507 | `24219fcd36518ce19e2f306f8ba1e086c61a68b7233e7c7f1cf5669dc6f6b3e2` |
| 10816087303 | `8693906f71a5a0c1cf0a4b3ac6c338cc5c83653bdbdfc0888c83858e2710976b` |
| 10815623160 | `f37788e6810d32637f8810928ff71865c912d10e62fe63de9248b26af5df557b` |
| 10815718972 | `bc80438675373437458dcd369d3b7159d64a3c8ffaa1d02a8ccd93c0d92c691e` |
| 11703138985 | `0fd2db1eec2154dbb82462f603769d4ae462e3a33abb42c5717f94bcad1639ce` |
| 11703033512 | `c525ae04467fff7beb4a5a9e197946c4ec9050a65931f0577cd7fd1aef9dd225` |
| 11711417040 | `4aed08e1b70a3e802c6d89f970702e9afeb13e6f0938736fcc11c5b3ea4ef6c4` |
| 11710959866 | `2714aee73c187a21ffa5c5f85e298d2c32a9d57131c65d500073e5b89bcae46d` |
| 11712415123 | `ff8ed048f59c1c535c15c793b981ab0ad99f522b9323f935ca349e4a17f3c4bf` |

## 3. 架构演化：每一步减少的成本及未覆盖的生产边界

### 3.1 Phase A：Resolved Flow State 省查表，连接 gate 仍在

真实 RPC / StreamLoadTest 固定 baseline 为 `56c643cd308f294cb03df79d9f1214fb8292affb`，candidate 为 `3dab2b7599eda5ce9ead644352013e6d4a2aa0ae`；microkernel 则在同一个 candidate binary 中比较 keyed/resolved 两种调用。身份来自 artifact 的 `env/revisions.txt`、raw JSON 和 [run 35862800731](https://github.com/SunSi12138/SharpLink/actions/runs/35862800731) 的实际 checkout/build 日志，不能把这两种 A/B 当作同一个实验。

Phase A 的 `ResolvedSendCreditLease` / `ResolvedReceiveCreditLease` 绑定 owner、state、generation。反复 item 操作可以绕过流状态字典查询，但 resolved overload 仍进入同一个连接 `_gate`：`TryAcquireSendCredit` L166–185、`ReturnUnsentCredit` L425–452、`AcceptReceived` L609–623、`RecordConsumed` L649–688。Keyed 与 resolved 的 receive-pair 均是每 item 两次连接 gate；只有字典查询从两次降为零，生命周期开始仍需 resolve，terminal / tombstone / pool / generation 检查仍存在。[固定源码](https://github.com/SunSi12138/SharpLink/blob/3dab2b7599eda5ce9ead644352013e6d4a2aa0ae/src/SharpLink.Runtime/StreamFlowController.cs#L609)

microkernel 的顺序 case 在 `Measure` 里每轮重新创建 controller / lease arrays，再运行固定长度；所以短流数据含初始化摊销。Contention probe 是多个 Task.Run 争用 controller，内部做 AB/BA，使用全进程分配与 `Monitor.LockContentionCount`；它不等价于真实 socket RPC，也没有每次 lock 的 wait/hold 时间分解。[measure / contention 实现](https://github.com/SunSi12138/SharpLink/blob/3dab2b7599eda5ce9ead644352013e6d4a2aa0ae/test/SharpLink.Benchmarks/ResolvedFlowStateEvidenceRunner.cs#L179)

| Phase A microkernel 指标 | JIT PGO ON | JIT PGO OFF | 实际 NativeAOT |
|---|---:|---:|---:|
| 历史 long-lived 分组 ns/item 中位下降 | 22.39% | 19.82% | 12.58% |
| send-no-wait ns/item 中位下降 | 23.67% | 21.36% | 16.61% |
| receive-pair ns/item 中位下降 | 28.62% | 28.68% | 23.32% |
| send-contention ns/item **增加** | 71.54% | 29.98% | 28.79% |
| receive-pair-contention ns/item **增加** | 120.49% | 62.85% | 46.32% |

这里的“下降”严格采用历史公式 `(key_ns-resolved_ns)/key_ns`，不是吞吐提升百分比。Long-lived 分组汇总不能覆盖 contention 的反向结果；`phase-a-recomputed.json` 保留每个 case、每轮配对值，避免只呈现中位好消息。

历史 Callgrind receive-pair c32 结果：JIT instructions/item 5,547.14 → 4,638.72；NativeAOT 611.32 → 514.31。它是模拟器归因，不是硬件 PMU，更不能把 JIT / AOT 的这些不同全程序指令数直接比较为运行时速度。原 `counter-summary.md` 明确说明 PMU 不可用；原输出 `counters/callgrind-*.out` 与 logs 完整保留。该结果支持“省查表确有 CPU 工作减少”，没有证明剩余 lock、调度或端到端总成本同幅减少。

#### 3.1.1 真实生成式 RPC 把理论收益压到了接近噪声或负值

相同 harness 被复制到两个固定 revision。三轮 AB/BA/AB，TCP / SharedMemory，JIT PGO ON，30 个 warmup，5 秒每个 measurement，c1，默认生产路径；历史摘要用两臂各自三轮中位数的比值，**不是** #803 四臂实验的 per-round paired median。原始记录均 `validationFailures=0`。不能把两个聚合方法、两个旧基线混合求累计提升。[完整 workflow](https://github.com/SunSi12138/SharpLink/blob/3dab2b7599eda5ce9ead644352013e6d4a2aa0ae/.github/workflows/resolved-flow-state-performance-evidence.yml)

| 场景 | TCP RPC/s 或 item/s 变化 | SharedMemory 变化 |
|---|---:|---:|
| Server1×16 B | −0.70% | +0.28% |
| Server100×16 B | +0.15% | −3.43% |
| Server100×4 KiB | +0.24% | −11.88% |
| Client100×16 B | −3.77% | +9.35% |
| Client100×4 KiB | +0.60% | −0.93% |
| Duplex100×16 B | −2.13% | −3.95% |
| Duplex100×4 KiB | +2.65% | −4.86% |
| 100-item 场景跨场景中位 | +0.20% | −3.69% |

这些数字不支持“Phase A 真实生产 RPC 普遍提升 20%”。短 server stream 约新增 47.3 B/RPC 的历史分配可见，但当前证据不能把这些字节分解到具体 closure / object；应由 allocation trace 再归因，不能凭源码猜分配尺寸。

额外 c8 的 StreamLoadTest 是真实 runtime，Int32 streaming 操作，显式 stream 8 KiB / connection 64 KiB；不是默认窗口。原记录 `FormalComparable=false`、`RecorderMode=off`，没有 per-RPC latency 样本；duration 2 秒 / warmup 1 秒，三轮 AB/BA。十千项长流 TCP c2s/s2c/duplex 分别 −0.36% / −1.51% / −2.16%，SharedMemory +2.30% / −4.80% / −1.84%。两臂使用完全相同显式窗口，但该历史窗口选择不能拿来替代本轮默认窗口验收。

#### 3.1.2 NativeAOT 证据确实执行，但层次只到 microkernel

[job 107186869793](https://github.com/SunSi12138/SharpLink/actions/runs/35862800731/job/107186869793) 原日志 L378 有 `Generating native code`；L558 直接运行 `$ROOT/aot/bin/SharpLink.Benchmarks`。ZIP 里还包含原生 ELF 本体：SHA256 `415d30190e8b84c7766ee8e49b164f2cec08cf3adae263f09a428e774c61d560`，ELF build ID `b6226d8c0142dfc01c2949a166865500a06daac3`。本次又在本地实际运行该原始二进制的一组 1,000-item keyed/resolved 控制，退出码 0、两路 checksum 均 48,000；记录在 `phase-a-native-local-execution.json`。这是二进制可执行性与证据层次确认，极短单次局部运行不具备统计性能结论，不能作为 AOT RPC A/B。

### 3.2 B0：拆分 send/receive gate，未减少每 item 协调次数

Phase B 的 frozen controller 指向 `65892a1d9cef5a79b6afb865e667935a4b5b5e33`；生成器校验 `StreamFlowController.cs` / `.FirstAdmission.cs` Git blob 为 `4354ef1793fc256d0d9bf729a957f1262098086d` / `b0857e644a7d53ada534fc3464ac185d9724dad2`。[固定生成脚本](https://github.com/SunSi12138/SharpLink/blob/7d01a3f6ef3e1efbbf03f9b4ca33fbedd4769977/eng/prepare-flow-state-phase-b.py)

生成脚本只将八个 receive entry points 的 `_gate` 改为 `_receiveGate`；terminal `Complete` 按 send → receive 顺序拿两个 gate，避免两个方向并发破坏共享 terminal / pool 状态。Non-instrumented research rows 每 item 仍有两次 gate。Gate timestamp 单独 instrumented runs，不与低开销数值混算。[固定生成器](https://github.com/SunSi12138/SharpLink/blob/7d01a3f6ef3e1efbbf03f9b4ca33fbedd4769977/eng/prepare-flow-state-phase-b.py#L30)

在 [split-gate correctness job](https://github.com/SunSi12138/SharpLink/actions/runs/35893121186/job/107290241808) 上，旧版完整 1,877 tests 与 flow-controller 54 tests 实际通过。但这只验证对应历史 controller / 旧测试集，不能代替今天包含 #840 / #841 的新基线验证。

B0 模型方向性与并发相关：JIT PGO1 duplex c1/16 B 292.71 → 162.66 ns/item，c128/16 B 186.07 → 175.32；同 c128 的 receive-only 213.40 → 243.44。它支持“拆方向锁可以降低双向干扰”，不支持“同一方向连接争用已消失”，更没有独立真实 RPC 端到端正证据。

### 3.3 B1 / B2：连接 owner 与有界 stream-local grant

[GrantAuthority](https://github.com/SunSi12138/SharpLink/blob/7d01a3f6ef3e1efbbf03f9b4ca33fbedd4769977/test/SharpLink.FlowStatePhaseB/GrantAuthority.cs) 的 connection owner 是单 reader、有界 Channel（默认 `4×maxStreams` 容量），跨线程 producer 有 stream-local `Lock`。B1 每个 item 都提交 acquire；B2 在无 waiter pressure 且 stream-local grant 足够时，普通 item 只在 stream gate 内扣 grant / credit、生成 struct Receipt；`Commit` 再进一次 stream gate。满队列等待 `WriteAsync`，每 stream 预分配 AcquireCommand / UpdateCommand，复用 `ManualResetValueTaskSourceCore`；不能称“完全无锁”。

模型信用等式：

`connection free + Σ(unspent grant + pending receipt + outstanding bytes) = connection window`

`stream credit + pending receipt + outstanding bytes = stream window`

这里 `grant` 是连接 authority 已预留给流但尚未送出的额度，不是第二份可独立扩张的信用。等待压力先发布 `_pressure=1`，再 `RevokeUnspent()` 扫所有 state 并逐一拿 stream gate；最坏是 O(active streams) 扫描，不是免费 reclaim。FIFO 每次 owner command 后扫描取消 waiter 和可承兑前缀；先检查 connection credit 再 stream credit，与当前 dev 的相关 policy 仍需共同对照。

当每 64 个 item 返回一次信用，16 B 消息的理论与实测命令次数一致：

| 方案 | Grant 摊销项 | WindowUpdate 项 | owner commands/item |
|---|---:|---:|---:|
| B1 | 1 | 1/64 | 1.015625 |
| B2 grant256 B | 1/16 | 1/64 | 0.078125 |
| B2 grant1024 B | 1/64 | 1/64 | 0.031250 |
| B2 grant4096 B | 1/256 | 1/64 | 0.01953125 |

因此最大模型 command 减少为 52 倍，不是 256 倍；WindowUpdate 仍需 owner 协调。模型 checksum、settled ledger、zero reusable allocations / bounded-queue backpressure 字段已逐条检查，runtime / Channel 内部原子操作故意记为 null，不能声称总 CAS 为零。[原始 probe](https://github.com/SunSi12138/SharpLink/blob/7d01a3f6ef3e1efbbf03f9b4ca33fbedd4769977/test/SharpLink.FlowStatePhaseB/GrantProbe.cs#L9)

| c128 send-only model | B1 ns/item | B2 grant4096 ns/item | 命令次数变化 |
|---|---:|---:|---|
| JIT PGO0，16 B | 593.26 | 29.35 | 1.015625 → 0.01953125 |
| JIT PGO1，16 B | 502.24 | 40.39 | 同上 |
| NativeAOT，16 B | 576.61 | 24.92 | 同上 |
| JIT PGO1，4 KiB | 487.05 | 531.32 | 1.015625 → 1.015625 |
| NativeAOT，4 KiB | 555.18 | 616.22 | 同上 |

这只是两个重复、固定流、credit 充足、定期本地模拟 peer update 的研究模型。每 stream 配 1 MiB，connection 配 `streams×1 MiB`；没有真实 socket、serializer、生成器、Request、Flush、DATA buffer 或独立 peer receiver。NativeAOT 在 [job 107290241811](https://github.com/SunSi12138/SharpLink/actions/runs/35893121186/job/107290241811) 有真实编译/执行日志 L740–1011，但同样是模型。B2 尚未通过独立完整 RPC 验收，不能说模型已经证明生产 Go，也不能说这些模型负值已证明所有 B2 生产架构 No-Go。

### 3.4 B2 publication：连接命令没增加，ownership 仍有成本

[PublicationProbe](https://github.com/SunSi12138/SharpLink/blob/7d01a3f6ef3e1efbbf03f9b4ca33fbedd4769977/test/SharpLink.FlowStatePhaseB/PublicationProbe.cs#L55) 在相同 B2 grant4096 与 64-item peer-update 节奏下，对照 immediate `Commit(receipt)` 与 `BeginPublication(receipt)` / `FinishPublicationAsync(publication, accepted:true)`。没有 serialization / transport，只有显式 writer lifetime；setup / stream open 在计时前，动态关闭 / pool 回收在计时后。

| 16 B publication model | legacy ns/item | writer-owned ns/item | 时间增加 |
|---|---:|---:|---:|
| JIT PGO1 c8 | 163.83 | 219.76 | +34.14% |
| JIT PGO1 c32 | 128.12 | 184.92 | +44.33% |
| JIT PGO1 c128 | 37.49 | 51.44 | +37.19% |
| NativeAOT c32 | 24.57 | 34.35 | +39.79% |
| NativeAOT c128 | 26.01 | 32.57 | +25.24% |

全部这些 case 的 owner commands/item 仍为 0.01953125；也就是**即使连接级协调次数相同，ownership 状态更新、额外 stream lock 和完成路径本身仍能增加成本**。但这些时间是模型整体成本，无法精确把增量逐纳秒分摊到 lock、ValueTask 或字段写入。由于两个重复、短计时、PGO 与调度噪声明显，不能把这个百分比直接乘到 #803 RPC 回退上。

生产迁移还有一个关键不匹配：模型 `WindowUpdateAsync(Lease lease, int bytes)` 自带 generation；实际 wire 只有 requestId / streamId。模型注释明确承认这一点，旧 generation 的更新可以直接拒绝；生产必须靠 tombstone / key reuse policy 防止旧更新归到新流。其“窗口守恒通过”不证明实际 wire ABA 已解决。[ApplyWindowUpdate / publication / retirement](https://github.com/SunSi12138/SharpLink/blob/7d01a3f6ef3e1efbbf03f9b4ca33fbedd4769977/test/SharpLink.FlowStatePhaseB/GrantAuthority.cs#L361)

### 3.5 B3 matched Ready Writer：同发送路径，固定生命周期

[`ReadyWriterCoordinator`](https://github.com/SunSi12138/SharpLink/blob/9628bbc6782dc8a9696e250caa5a984c53d69a43/test/SharpLink.Benchmarks/ReadyWriterCoordinator.cs) 对 A-ready/B3-ready 使用相同 bounded producer ring、真实 SendPump、同一接收控制器、同一 transport 和 flush。两侧都没有每 item emission waiter，因此其 B3 收益不能简单说是“把 A 的每 item flush/TCS 去掉”。B3 将 producer 的已解析流信用申请移到 writer，省掉完整 producer-side controller 的每 item credit gate，保留 writer 的 plain stream/connection debit。

原型固定 128 个 stream slot，数组定位流，构造/注册/任务建立在 measurement start gate 之前。返回只允许 balanced updates；没有 late/duplicate credit、terminal generation、动态注册/退休、真实生成器调用、业务异常、named stream 或 Request/EOF 完整路径。**原型仍独立 clamp stream/connection**，还显式拒绝超额归还；该模型范围不覆盖后来 #840 修复的语义，因此不得复制其 clamp 逻辑到当前 dev。

容量等待使用持久的 `ManualResetValueTaskSourceCore`，外层 `EnqueueAsync` 使用 `PoolingAsyncValueTaskMethodBuilder`。`Complete` 的 persistent target 固定属于同一流，正常释放只更新 owner 字段，不做动态 retirement lock。逐帧 buffer preparation 与 wire serialization 在 measurement 之内，但它是直接写入模式化 bytes，不是生产 codec/生成式 RPC 路径；流创建销毁的分配不在 timed region。

预算敏感性原始矩阵包括每 stream prepared cap 0 / 8 KiB / 16 KiB，ring 均为 16 槽；cap=0 仍受 item 数量边界约束，不能解释为无限 prepared memory。16 B 主体为 c128、每流 2,048 items、stream/connection 均 8 KiB；4 KiB 主体每流 128 items、connection window 512 KiB。q16 不会忽略中途通知，writer 在各帧之间检查更新；它是调度量子，不是 producer 得到的 speculative grant。[固定实验主体](https://github.com/SunSi12138/SharpLink/blob/9628bbc6782dc8a9696e250caa5a984c53d69a43/test/SharpLink.Benchmarks/ReadyWriterEvidence.cs)、原始 metadata/patch 见第 2 节 artifact。

Native [job 107697937556](https://github.com/SunSi12138/SharpLink/actions/runs/36018718001/job/107697937556) 日志保存 `dotnet publish`、`Generating native code`、直接执行 native host；JSON `DynamicCodeSupported=false`。896 条样本的 items/bytes、remaining queue、normal queue rejection 和进程 exit 均已独立核对。因此 Native 证据是真实的，但仍然是本小节的 matched transport。

#### 3.5.1 q1/q16 的可量化范围

在准备预算 **8 KiB/stream**、c128、16 B、2048 item/stream、stream/connection window 8 KiB、ring16、flush threshold16 KiB、4 CPU 下，独立重算得到：

| Runtime/transport | A-ready q16/q1 | B3 q16/q1 | B3 vs A，q1 | B3 vs A，q16 |
|---|---:|---:|---:|---:|
| JIT PGO ON TCP | +6.39% | **+36.20%** | +32.67% | +69.85% |
| JIT PGO ON SharedMemory | +6.45% | **+47.80%** | +20.24% | +66.93% |
| NativeAOT TCP | +3.01% | **+24.11%** | +25.56% | +51.28% |
| NativeAOT SharedMemory | +1.32% | **+36.23%** | +23.99% | +66.72% |

PGO ON TCP B3 q1→q16 的 median：writer turns/item **1→0.0625**；WireUpdateNotifications/item **0.4992→0.0657**；EventChannelReadCalls/item **0.4997→0.1083**；RingCapacityWaits/item **0.5947→0.0660**；CPU **1430.82→1091.18 ns/item**；allocated **62.778→17.480 B/item**。这些变化一起发生，不能将 339.64 ns 差额全解释为 LinkedList remove/add 的成本。

4 KiB controls 的 q16/Aready 收益仅接近零或数个百分点，甚至负数；Native 4 KiB B3/A q16 为 TCP +0.41%、SHM +0.17%。数据并不支持所有消息大小都获大幅收益。完整 PGO 0/1、Native 和 4 KiB 表在 `prototype-recomputed.md/json`。

**对 #803 的问题“q1 究竟损失多少”只能回答：同 #803 主体的量化值尚不存在。**上述实验对 q 给出了可信的研究范围，但 #803 最鲜明的 −31% 对照在 c1 Duplex，而模型的强信用回报聚合效应来自 c128。q16 仍可能减少 c1 的 list 操作，却不能无证据地声称会复现 c128 的 36%～48% 变化，更不能把它与别的改动收益相加。

### 3.6 #800 / #803：从 handoff 接口到真实 RPC，生产边界不断显现

下表只压缩呈现有架构或失败意义的提交，完整 9+14 个 patch 在证据档案中。

| 阶段 / 固定提交 | 变化 | 审计意义 |
|---|---|---|
| #800 [`22372854`](https://github.com/SunSi12138/SharpLink/commit/223728541990711ab9ca45f5193708fb279efef3) → [`31bc10c1`](https://github.com/SunSi12138/SharpLink/commit/31bc10c14c01bc6497aeb56b21ac424af366c431) | SendPump handoff、immutable completion target | 先解决帧移交身份，并非已接通 RPC。 |
| #800 [`df3c837e`](https://github.com/SunSi12138/SharpLink/commit/df3c837e0972914633fe81053d8caa534bf6b3bb) → [`c9b52d2e`](https://github.com/SunSi12138/SharpLink/commit/c9b52d2ef3a750ef15b32268c4c708091c02bb8e) | ordinary/progress 交错、完成结算 | 研究 scheduler 必须适应现有发送队列与 Flush 语义。 |
| #800 [`362f9735`](https://github.com/SunSi12138/SharpLink/commit/362f973566433835c3100a6f4bfd7bdd2a052316) → [`45339431`](https://github.com/SunSi12138/SharpLink/commit/453394316a05c0402b88b68e9a38e7546eb365f2) | WriterFlowScheduler、Serialize 前 reservation、terminal fence | 含生产约束仍不代表生成式 RPC E2E 已测。 |
| #803 [`e079c227`](https://github.com/SunSi12138/SharpLink/commit/e079c227330fac920c2b5b00396839890b33318b) → [`01c75c3d`](https://github.com/SunSi12138/SharpLink/commit/01c75c3df338de0ebb01f9eccf41fd915bbef134) | handoff、独立 credit owner | 新 owner 与动态状态基础。 |
| #803 [`e26be5e3`](https://github.com/SunSi12138/SharpLink/commit/e26be5e3fddb3767ed22ccd3f7934ff6c47c0987) → [`6181f03c`](https://github.com/SunSi12138/SharpLink/commit/6181f03c9bb4c01861eed5fd18e4856a61047849) → [`1c76ad13`](https://github.com/SunSi12138/SharpLink/commit/1c76ad131eda1333cf62c87f87a37a5f2b2fc8c8) | 重入 abort 先 red，增加 cleanup use pin；随后修新 fixture 的 immediate-IsCompleted 假设 | [首次 red](https://github.com/SunSi12138/SharpLink/actions/runs/38110719730) 两个旧 key 污染新 generation 的断言失败；[runtime 修复后](https://github.com/SunSi12138/SharpLink/actions/runs/38111130100) 新 fixture 还有一失败；[后续完整 green](https://github.com/SunSi12138/SharpLink/actions/runs/38111320489) 2,020/2,020，原两条 red 断言保持不变。 |
| #803 [`4dca8c1a`](https://github.com/SunSi12138/SharpLink/commit/4dca8c1a9758d5fac084578033fb8585a48eed56) → [`57ced2e8`](https://github.com/SunSi12138/SharpLink/commit/57ced2e8e0f5d8cd6aac094148f8f9c4e252d5ea) → [`d833119d`](https://github.com/SunSi12138/SharpLink/commit/d833119da9529569eed61114331da2b5a0fca455) | session bridge、actual client/deadline、TCP/SHM 真实测试 | Request 先行、客户端结束与连接取消开始进入真正生产路径。 |
| #803 [`499771a6`](https://github.com/SunSi12138/SharpLink/commit/499771a68990d4eee33076e87f2a1cc8db3e8701) → [`95be5e67`](https://github.com/SunSi12138/SharpLink/commit/95be5e67292f26da0038cf3400609f5f8d5f62dc) | red 证明两个 24 B packet 只记 24 B；准备预算 acquire 移到 slot wait 前 | [原 red CI](https://github.com/SunSi12138/SharpLink/actions/runs/38117572272) 必须保留。同 run 的另外 12 项 fixture generator registration 失败不是同一个 runtime 缺陷；不能混淆。 |
| #803 [`55521de9`](https://github.com/SunSi12138/SharpLink/commit/55521de9fdc40dbc5b56458545ae7088d69bd007) → [`39f27f08`](https://github.com/SunSi12138/SharpLink/commit/39f27f08d9704e19b5ec7523f4a9f7494fe828cc) | 独立生成 fixture assembly；实际 RPC measurement 与 source binding | [PR Fast](https://github.com/SunSi12138/SharpLink/actions/runs/38140928742) 2,059/2,061，已有两项取消失败；[package smoke](https://github.com/SunSi12138/SharpLink/actions/runs/38140928744) 通过不能抵消它。 |
| #803 [`8ac4c849`](https://github.com/SunSi12138/SharpLink/commit/8ac4c84938d193c9a9659e9facd71f1fff82968a) | 增加诊断断言上下文和八轮完整压力 | [另一次 PR Fast 绿](https://github.com/SunSi12138/SharpLink/actions/runs/38142405696) 与 [八轮中三轮红](https://github.com/SunSi12138/SharpLink/actions/runs/38142405753) 同时成立；不能择一报告。 |

## 4. #803 原始真实 RPC 性能与八轮完整测试

### 4.1 实际 RPC 性能：原始记录重新计算

环境：Ubuntu 24.04.5 LTS、.NET 10.0.12、SDK 10.0.112、4 个逻辑处理器，Intel Xeon Platinum 8573C VM。每次采样独立进程，PGO=1、TieredCompilation=1、ReadyToRun=0；c1 64 次预热、c8 16 batch 预热、c32 8 batch 预热；1.5 秒 measurement、四轮四臂旋转。数据来源为 artifact 内 `env/`、`measure/status.json` 与全部 raw JSON。

`eng/issue735/analysis/recompute_historical.py` 重新检查了 416 个文件、source label、writer mode、零 validation failure、collector 的全部完成/错误状态。百分比计算严格复现历史口径：每轮以 dev-a/dev-b 的中位数（两个值即算术平均）作 reference，然后报告四个 paired ratio 的中位数；不是先分别聚合吞吐再相除。

| 场景，c1 | TCP RPC/s Δ | SHM RPC/s Δ | TCP CPU/RPC Δ | TCP 额外 B/RPC |
|---|---:|---:|---:|---:|
| Server1×16 | −15.49% | −15.65% | +23.11% | +3004.09 |
| Server100×16 | −24.03% | −5.27% | +35.84% | +5975.80 |
| Client100×16 | −8.17% | −14.33% | +14.86% | +6034.38 |
| Duplex100×16 | **−31.18%** | **−32.82%** | **+53.81%** | **+11491.46** |
| Server100×4096 | −4.26% | −4.71% | +6.23% | +10622.26 |
| Client100×4096 | −5.14% | +5.90% | +7.06% | +15114.01 |
| Duplex100×4096 | −14.24% | −25.90% | +20.39% | +10677.21 |

完整 26 cell 及每轮结果在 `production-recomputed.md/json`。SHM Client100×4096 的 +5.90% 对照 dev A/A +8.52%，不足以证明收益。TCP c1 Duplex 四轮都回退，对 candidate 自己的 legacy 模式也回退 −32.26%，因此不能全部归于两个分支的其他差异。SHM c1 Duplex 的 dev A/A +23.06% 很大，−32.82% 的精确幅度不应写作普适常数。

测量边界也必须保留：c>1 的 latency 是整个 concurrent batch 的 latency；没有逐 RPC P95。Duplex 的 `item/s` 和 B/item 除数是单方向的 100 items，而一次双向 RPC 实际含两个方向的数据；不能把该字段当作双向总 wire item 数。全进程 CPU/GC 包括本地 client+server；没有把 Queue、CAS、TCS 和序列化逐项耗时分离的 profile。source SHA 是 harness 环境标签，并由 checkout/build 日志支持；#803 artifact 未保存两套 runtime 二进制字节或完整 assembly SHA256，因此新验收还需更严格的二进制绑定。

原 collector 的成功记录以 `error=None` 和 complete 状态表示进程成功，并非每条都另存 numeric exit code；其成功含义由固定 collector 源码和作业完成日志支持。这里不把 416 条 RPC 成功采集写成全部测试、全部 runtime 或性能验收成功。[测量 job](https://github.com/SunSi12138/SharpLink/actions/runs/38140928821/job/114474471765)

### 4.2 历史压力：八轮中三轮失败必须完整保留

`8ac4c849` 的 [run 38142405753](https://github.com/SunSi12138/SharpLink/actions/runs/38142405753)、[job 114478783494](https://github.com/SunSi12138/SharpLink/actions/runs/38142405753/job/114478783494) 每轮完整 2,061 tests，均无 skip。真实退出码为 `2,0,2,0,2,0,0,0`。

| 轮次 | 结果 | 原始观察 |
|---|---|---|
| 1、3 | 远端取消失败 | `received=SharpLinkException: The stream is closed., connected=True, prepared=0, retained=1` |
| 5 | 客户端取消后 prepared 未结算 | `connected=True, prepared=42, retained=1, cleanup=none` |
| 2、4、6、7、8 | 当轮完整套件通过 | 不抵消另外三轮的失败。 |

仅凭旧 round5 日志能证明**调用完成观察点的资源边界失败**，不能证明永久泄漏、重复返还 buffer 或 send-credit 泄漏。本次第 7 节的受控实验进一步证明了一个在回调屏障释放后依旧残留 42 B 的可达路径；两层证据分别保留。

## 5. 失败根因分级

### 5.1 已证实的机制或实测事实

| 问题 / 机制 | 直接证据 | 结论边界 |
|---|---|---|
| #803 真实 RPC 的大面积吞吐、CPU 和分配回退 | 第 4 节：416 records、26 cells；原 artifact 11703138985 | 足以拒绝旧候选；不提供各组件独立耗时份额。 |
| 远端异常在 terminal 按 key 重开时变 generic Closed | 原 stress round1/3；本次两版 red 均 2/2 精确失败，v2 对应 green 精确身份通过 | 已证实此可达因果链；没有证明历史每次失败都来自唯一 stack，也未覆盖退休先完成的窗口。 |
| 取消 callback 未执行前被 client finally Dispose，prepared 清理可落空 | 原 stress round5；本次真实 ClientConnection 的另 2 个确定性 red，完成及释放回调后均 42 B | 归因 green 显式结算 immutable lease 后为 0，且保留 iterator 原异常；不代表其他所有出口皆正确。 |
| 历史重入 abort 可让旧 keyed cleanup 影响替代 generation | `e26be5e3` 两项 red，`6181f03c` cleanup use pin，`1c76ad13` green，见第 3.6 节 | 该 pin 缺口得到历史修复；不能由一次历史 green 推出当前全部 ABA/lifecycle 已覆盖。 |
| 已物化 packet 等 stream slot 时曾未占准备预算 | `499771a6` 两个 24 B packet / 仅 24 B accounted；`95be5e67` 预算 acquire 前移 | 物化所有权与 budget accounting 必须同步建立；不是一种性能优化可以省掉的记账。 |
| q16 在 matched transport 同时减少 writer turns、窗口更新和容量等待 | 第 3.5 节 896 samples 与原 metrics | 这是复合调度效应；只能给该模型里的幅度。 |
| #803 普通 DATA 多次 stream gate、两种内存预算 RMW 和动态生命周期分配存在 | 第 6 节固定源码操作下界 | 不将源码次数冒充真实 contention、CAS retry 或 ns/item profile。 |
| 本次 v1 overlay 改坏全局 `Open` 异常契约，导致三项旧重入测试失败 | run 38163848907 完整 green 3 红；v2 恢复原 `Open` 后对应三项不再失败 | 归因 overlay 自身问题必须记录；没有削弱旧断言来取得 green。 |
| 本次 v2 完整套件出现 decode-entry 3 秒断言失败 | run 38164483629 的 `EarlyDisposeShouldNotPoolWhileProducerIsDecoding` | 是真实未解释失败；后续固定矩阵未再出现不等于已修复。 |
| 未改历史 control 在新 H/G 矩阵再次出现原 remote-cancel 失败 | run 38165035402 pair3-H；`control.patch` 为 0 B | 旧问题不是仅由新增 fixture 引出；workflow 仍红，G 四轮通过仅支持局部归因修正。 |

### 5.2 有源码或相关测量支持的合理怀疑

| 假设 | 为什么值得检验 | 尚不能写成事实的部分 |
|---|---|---|
| q1 生产调度使高并发小消息收益损失明显 | prototype q16 提高 B3 吞吐 24%～48%，并减少 owner turns 与等待 | #803 同主体 q1/q16 RPC 尚未测；不能把 c128 模型增益直接加到 c1 的 −31%。 |
| dynamic lifecycle、普通 async 挂起和 TCS 压力是 +CPU/+allocation 的重要来源 | #803 新路径与旧 prototype 在这些操作上不同；publication model 同命令数也变慢 | 没有独立 allocation stack、采样 profile 或 ablation，不能给每项占比或声称 1 Task/item。 |
| Request 前驱 Flush 延长短流启动或 terminal 关键路径 | unresolved client resolve 实际调用 `FlushSendQueueAsync().AsTask()` | 没有对应 fence ablation；不能省掉 Request→DATA 协议顺序来测试收益。 |
| 本次 v2 decode-entry 失败可能与线程池调度或新增取消 fixture 的阻塞回调有关 | 旧测试 `Task.Run` 后同步等 3 秒；新增 fixture 也使用阻塞 callback | 无 runtime stall trace；固定 H/G 两臂未再出现，不能确认为 ThreadPool 饥饿、基线 flake 或 overlay regression。 |
| TCP `snd_wnd < MSS` 与缓慢发送节奏存在因果关系 | `rwnd_limited≈100%`、Send-Q 积压且每 5 秒仍前进 | 单次 `ss` 关联不能区分具体内核分段/ACK/window-update 机制，需要 packet capture 或匹配干预。 |

### 5.3 尚未测量、不能补写成功结论的项目

| 缺口 | 本报告允许的判断 | 所需证据 |
|---|---|---|
| Queue、Task/ValueTask、TCS、回调、锁、CAS、budget、handoff 各自耗时 | 第 6 节只有明确条件下的源码操作下界 | 当前固定基线、相同 harness 的分配/profile 与有控制的逐项实验；不得把互相影响的差值相加。 |
| #803 或新 B3 在真实 RPC q1/4/8/16/32 的敏感性 | 现只有 prototype q1/q16，不足以量化生产损失 | 同一 production subject、相同窗口/codec/Flush 的 interleaved A/B。 |
| 改进 B2、混合 grant/owner 在当前 dev 的生产 Go | B2 model 证明可摊销连接命令，但没有生产验收 | 默认窗口、动态创建销毁、压力 reclaim、取消、JIT PGO 0/1 与实际 NativeAOT RPC。 |
| codec/snapshot/compression 的独立性能贡献 | 源码证明这些工作在 producer；不能断言移到了 writer | 生产 codec 的真实 profile 与 matched workload。 |
| 每流/每连接精确 CLR 常驻 bytes 与内部 Channel 原子次数 | 可列对象与字段，不能凭对象数给准确字节；内部 RMW 留空 | JIT/AOT 各自布局、allocation trace，以及运行时内部协调测量。 |
| mixed-size 队首公平性是否是候选独有回退 | #803 与当前 dev 都先看 connection credit；prototype 次序不同 | 带 stream-blocked/connection-blocked/oversized 组合的同基线确定性对照，不先改 FIFO policy。 |
| 全部 late WindowUpdate、key reuse、退役先于 terminal 等生产边界 | 模型自带 generation，wire 不带；局部 red/green 无法覆盖所有状态交错 | 当前协议下真实 key-only API、tombstone/ABA 与 teardown 的完整不变量测试。 |

## 6. #803 的生产成本、所有权与信用责任

### 6.1 普通 DATA 的静态操作下界

下面只计候选新增发送调度的显式代码，不含 codec、pool、transport、遥测内部实现和 CAS retry。条件为：已解析 lease、非 terminal、stream 与 connection 额度足够、global prepared budget fast path、ring 未满、stream 已在 ready list、无取消和 queued change。它是**源码操作模型，不是实测 ns 分摊**。

| 操作 | 每 DATA 数量 | 位置 / 说明 |
|---|---:|---|
| producer stream gate | **4 次** | Enqueue 验证及 producer use、slot 检查、publication、finally 释放 producer bit。 |
| writer stream gate | **3 次** | TryTake、Released、TryRetire 的早退出也进 gate。 |
| global preparation RMW | **至少 2** | `_prepared.AcquireAsync` 成功 CAS；TryTake 的 `_prepared.Release` Interlocked.Add。无争用路径**没有** prepared gate lock。 |
| 已有 SendPump queue-budget RMW | **至少 2** | admission 的 CAS；flush settlement 的 Interlocked.Add。B3 并未省掉这两个共享计数。 |
| stream frame Queue | 1 enqueue + 1 dequeue | 存的是 PreparedFrame struct，不能说每 item 都 new 一个 Queue 对象。 |
| ready LinkedList | q1 下 1 remove，仍有 DATA 时 1 add | 这部分 owner-only，无需跨线程 CAS，但仍有 cache/branch/write 成本。 |
| immutable completion | 1 次 | buffer return 后 `WriterReadyCompletion.Complete`，再进入 Released/retire。 |
| changes queue + wake | 可合并；非固定 1/item | 从未 scheduled 变 ready 或 WindowUpdate/abort 等才 Notify；每次 Notify 有 ChangeQueued Exchange 和 pump signal。 |
| ValueTask/task allocation | **不能固定写作 1/item** | 全同步完成可零分配；slot/budget/deadline/Request 等挂起会使普通 async 状态机及 WaitAsync/TCS 分配。 |

依据：[Publication](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.Publication.cs)、[Scheduling](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.Scheduling.cs)、[PreCreditSerializedBudget](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/PreCreditSerializedBudget.cs)、[SendPump](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/RpcSession.SendPump.cs)。

未合并 change 时还需每个 drained change 的 Exchange、stream gate 和 retirement gate；真实 WindowUpdate 路径还先 map gate 查表，再 stream gate 累积 PendingCredit。它确实建立了一个 send-credit authority，但“单 owner”没有把所有连接共享协调消除：生产者/owner 仍在 prepared-byte reservation 和 wakeup 状态上竞争，数据本身通过 stream gate 握手。

每个新 stream 分配一个不回池 StreamHandle、Lock、`Queue<PreparedFrame>`、16 槽数组、LinkedListNode；有压力再建 SpaceChanged TCS，terminal drain 建 DrainWaiter/TCS/list。每连接还有 map/dictionary、changes ConcurrentQueue、ready list 和独立预算。不能从源字段个数给精确 CLR 常驻字节；需要真实类型 allocation/layout 测量。历史观察的 +3 KB 短 RPC 分配包含整个生产调用生命周期，不能全部算为这些对象。

### 6.2 序列化、压缩和 Request 前驱 Flush

#803 [`RpcSession.WriterOwnedSerialization`](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/RpcSession.WriterOwnedSerialization.cs) 在 producer 侧做 sizing/snapshot、SerializeSized/Serialize、ReleaseSnapshot、compression/outbound preparation 和 deadline checks，随后把完成的 packet 交给 owner。因此它没有把用户 codec/compressor 直接放到 SendPump 回调里。不过这些工作仍处于每个实际 RPC 的关键路径，与模型直接填 bytes 完全不同；snapshot 和 compression 的真实成本未被独立 profile。

`Buffers.Rent(int)` 的参数是 `maxWrittenBytes`，不是立刻分配该容量的 byte[]。不能看到 `NegotiatedMaxFramePayloadBytes` 就断言每 item 都实际租了一块完整最大 frame 大小的数组。

### 6.3 prepared、writer-owned、send-credit 各自的唯一责任

| 阶段 | packet owner | memory 责任 | send-credit 责任 | terminal / cleanup |
|---|---|---|---|---|
| codec/snapshot/compression | producer | writer 尚不拥有 packet；producer 函数 finally 处理异常 | 尚未 debit | snapshot清一次；validation自身返packet的路径必须先清owns标志。 |
| Enqueue 已接收、预算/slot等待 | Enqueue producer | ownsPacket/ownsBudget 标志；ProducerActive pin | 尚未 debit | Abort清 keyed budget waiter；producer finally返尚未入queue packet。 |
| stream.Frames queued | stream scheduler | prepared-byte reservation | 尚未 debit | Abort从Frames移出、逐packet return并release预算。 |
| TryTake成功 | SendPump pending batch | pump budget先reserve，prepared预算后release | 唯一owner在detach前按decoded creditBytes debit | WriterReferences pin；producer/Abort不得拿回该packet。 |
| Flush成功/失败 settlement | SendPump到buffer pool | pump release预算并调用immutable completion | Flush失败不等于“未发送”，不能全额ReturnUnsent | Released减writer refs/结算drain；连接终止负责不能再安全复用的半写状态。 |
| remote abort/WU | owner | preparation取消与writer-owned分开 | remote abort恢复本stream债务一次；WU实际delta联动回给conn | 退休要求ownerClosed、credit full、无writer refs、无producer/cleanup uses、无frames。 |

额度等式（quiescent 时）为 `connectionCredit + Σ(streamWindow − stream.Credit) = connectionWindow`，oversized允许stream credit暂为负但不能凭空加许可。#803 [Scheduling L18-L24](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.Scheduling.cs#L18-L24) 已采用 `delta=min(pendingReturn, streamWindow-stream.Credit)` 同时更新两级额度，**它的 B3 分支不是 #742 原型的独立 clamp**。然而旧 legacy、旧研究基线仍未含后来 #840/#841；历史绿灯不能当作当前 dev 兼容证明。

SendPump 普通/进度预算和 ready/prepared budget 并非同一生命周期的重复“额度账本”：一个限制等待 protocol credit 的准备帧，一个限制已经交给发送队列的帧；它们都是内存预算，不是第二份连接窗口。TryTake 会短暂同时持两种 reservation 才交接，保证失败前不丢所有权；成本上确实多出 shared RMW。进度帧保留 headroom，64 个普通帧间检查进度，每次最多256；ordinary/ready之间各16帧有界交替。这里的16是**队列类别 interleave**，不能称作 stream scheduling quantum16。

`#840` 相关边界不能被“旧版都没有修复”一笔带过：**#803 的 B3 `DrainChanges` 已按实际 stream-return delta 联动返回 connection credit；旧 legacy 和 #742 matched prototype 才仍有 independent clamps。**这份源码事实既不允许复制旧 prototype 的记账，也不允许凭“B3 单 owner”断言它与当前 #840 的所有 late/duplicate/update 场景完全等价。[当前 #840 固定提交](https://github.com/SunSi12138/SharpLink/commit/45e5549587c9c8690f245a95a9edcc7c14634718)

### 6.4 生命周期状态机：由字段推导的审计模型

以下是对旧源码责任的审计抽象，不是新增状态表：未注册 → attached/open → queued/prepared → writer-owned（可同时还有 queued）→ owner closed/abort → 满足 credit、writer reference、producer/cleanup use、queue 全部退休条件 → detached。`ProducerActive` 的高位 cleanup uses 负责可重入 keyed budget 清理期间 pin 住旧 key；`WriterReferences` 则覆盖 SendPump 已拿走而尚未结算的帧。两种 pin 不能互相替代。[Scheduler 状态](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.cs)、[Abort / publication](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.Publication.cs)、[TryRetire](https://github.com/SunSi12138/SharpLink/blob/39f27f08d9704e19b5ec7523f4a9f7494fe828cc/src/SharpLink.Runtime/WriterOwnedStreamScheduler.Scheduling.cs#L144)

terminal 不能仅依据“producer 已结束”跳过上述责任；远端 cancel 选定的异常也不能在新一次 key lookup 中丢失。前者由本次 prepared-cleanup red 证明，后者由 terminal-exception red 证明。连接终止、Flush 部分写入、writer completion callback 重入属于不同交接边界，本报告没有把一条 callback 修复宣称为所有这些边界的完整修复。

## 7. 本次确定性 red/green 与完整 H/G 控制

精确 subject 为 `8ac4c84938d193c9a9659e9facd71f1fff82968a`。两个 red/green 都先校验 Git HEAD 与输入文件 SHA256，使用相同四个新增测试，不修改旧测试、断言、窗口或超时。新增代码只放在隔离历史 worktree，当前 dev 与 #840/#841 逻辑没有改动。

### 7.1 确定性 red 的两条因果链

1. **Abort 已选择异常 → terminal 重新 Open key → generic Closed 覆盖原异常。**测试在真实 session 的 DATA 已到 reader 后取得 immutable handle，持有该 stream gate 阻止 writer retirement，先调用真实 `AbortSendStreams`，再调用真实 `SendClientStreamCompleteWithWriterAsync`。两种原异常（Cancelled SharpLinkException / IOException）均被同一个 `The stream is closed.` 替换。v2 green 仅在 terminal 处复用现有 immutable identity，由既有 `ValidateOpen` 抛原异常，两个 exact ReferenceEquals 断言通过。此实验没有覆盖 terminal 查找前已完成 retirement 的另一窗口，不能称全部 lifecycle 已修好。
2. **取消 token 置位 → 较晚注册的 barrier callback 先执行 → producer 结束并 Dispose 尚未启动的 writer cancellation registration。**测试使用实际 ClientConnection，精确停在两个未送出 DATA 已占 42 B 的位置；较新的取消回调阻住同步取消线程，释放 iterator 后让 producer 完成。red 明确记录 `connected=True, prepared=42, afterCallbackRelease=42, cleanup=none`；支持“注册已撤销，prepared 清理不再发生于这条 callback”，不只是慢数毫秒。普通取消与 iterator 同时抛 IOException 两种参数均复现。v2 green 在 client finally 用 immutable lease 显式 Abort，完成观察前的 prepared 为 0，同时保留 iterator 原 IOException 的 ReferenceEquals。

### 7.2 全部真实运行结果

| 运行 | 编译 | Red focused | Green focused | Green 完整 suite | 解释 |
|---|---|---|---|---|---|
| [38163848907，v1](https://github.com/SunSi12138/SharpLink/actions/runs/38163848907) | 两臂成功 | 4/4 精确预期失败 | 4 轮 × 4/4 通过 | **2062/2065，3 失败** | v1 全局改变 `Open` 抛异常契约，破坏旧重入测试的 `catch (SharpLinkException)` 路径；并非证明 key pin 又提前退役。该 overlay 自身失误已明确归因，未改测试来掩盖。 |
| [38164483629，v2](https://github.com/SunSi12138/SharpLink/actions/runs/38164483629) | 两臂成功 | 4/4 精确预期失败 | 4 轮 × 4/4 通过 | **2064/2065，1 失败** | 全局 Open 完全恢复，v1 三失败消失；旧 `EarlyDisposeShouldNotPoolWhileProducerIsDecoding` 在等待 decode 进入的 3 秒断言失败。后续同机 control/green 矩阵未再出现此失败，仍未解释其原因，不标为已解决。 |

v1 三项失败是 `AbortCleanupMustPinTheKeyUntilKeyedBudgetCleanupFinishes(false/true)` 及 `NestedAbortMustNotReleaseTheOuterCleanupUse`。源测试在同步重入 callback 中尝试重新 Open 同 key，并只捕获 SharpLinkException 来证明 key 未解除 pin；v1 普遍重抛 IOException 使 callbackFailure 非空或 keyRemainedPinned 未置位，因此失败。v2 没有改变 Open 的新建/reopen 契约，只在已有流 terminal delivery 复用 immutable identity；这是缩小修复范围，不是放松断言。

v2 新的单项失败在 [旧 PooledAsyncStreamDispatcherTests L967–968](https://github.com/SunSi12138/SharpLink/blob/8ac4c84938d193c9a9659e9facd71f1fff82968a/test/SharpLink.UnitTests/Runtime/PooledAsyncStreamDispatcherTests.cs#L967)：Task.Run 提交 producer，当前测试线程同步 `entered.Wait(3s)`。原始 log 只证明没及时进入 decode，尚未执行 Dispose/pool 正确性断言；没有 runtime stall trace，不能断言它一定是 ThreadPool 饥饿、历史基线 flake 或 overlay regression。新增取消 fixture 使用会同步阻塞的 Task.Run callback，因此测试自身调度干扰也是待排除的假设。

### 7.3 固定完整测试控制矩阵已执行：旧未改 subject 再次失败，v2 四轮通过

[run 38165035402](https://github.com/SunSi12138/SharpLink/actions/runs/38165035402)、[job 114545713738](https://github.com/SunSi12138/SharpLink/actions/runs/38165035402/job/114545713738)、[artifact 11712415123](https://github.com/SunSi12138/SharpLink/actions/runs/38165035402/artifacts/11712415123) 在同一 runner 完成事先固定的 H/G、G/H、H/G、G/H。H 是完全未改的 `8ac4c849...`、2061 tests；G 是 v2、2065 tests；全部保持历史 TUnit 并行 8、旧断言/超时，失败后继续完成全部八轮，无临时追加重跑。

| 固定次序 | arm | 完整 tests | failed / skipped | exit |
|---|---|---:|---:|---:|
| pair1 / first | H | 2061 | 0 / 0 | 0 |
| pair1 / second | G | 2065 | 0 / 0 | 0 |
| pair2 / first | G | 2065 | 0 / 0 | 0 |
| pair2 / second | H | 2061 | 0 / 0 | 0 |
| pair3 / first | H | 2061 | **1 / 0** | **2** |
| pair3 / second | G | 2065 | 0 / 0 | 0 |
| pair4 / first | G | 2065 | 0 / 0 | 0 |
| pair4 / second | H | 2061 | 0 / 0 | 0 |

H pair3 的真实失败是原 `RemoteCancelMustUnblockPreparedStreamWithoutFaultingConnection`，原日志 L8–14 再次记录 `received=SharpLinkException: The stream is closed., connected=True, prepared=0, retained=1`。`control.patch` 为 **0 B**，不是新 fixture 引出的断言；这独立确认旧 subject 的原取消问题仍能在完整套件中发生。G 四轮完整套件均通过，支持两个诊断修复的效果，但不验证尚未覆盖的全部生命周期窗口，也不证明性能可接受。

本矩阵两臂均未再次出现 `EarlyDispose` 的 decode-entry 失败。**这没有解释此前 v2 的真实失败，不能把它改称“已修复”或“基线 flake 已证实”。**此前该失败和 v1 三失败均继续保留；没有为取得全绿而修改测试或调度环境。矩阵 workflow 因 H 的真实失败保持红灯。

来源绑定：harness commit `9910c41fdcf4b882b42e0711f919256dff44b343`，实际执行 [collector](https://github.com/SunSi12138/SharpLink/blob/9910c41fdcf4b882b42e0711f919256dff44b343/eng/issue735/historical_control_matrix.py) SHA256 `26f5cf95263d33598bed77e449bbb5eefc9d8be59d6ca051d3dc4ec80584ae43`。每臂记录 157 份 DLL/PDB/runtime-config hash 并在每轮前检查未变化；UnitTests DLL 的 H/G SHA256 分别为 `a04073866d0aa447e0889213807e6137b32af364edc200c22f7792474ea8afe0` / `aa65f5c63f9ff1067d9eefddbdbf079c40b77be7f87b441ff36b30c5dcd8c666`。两个 build 都成功，八轮实际总数和零 skip 已从原始日志独立核对。

ZIP 为 9,771,728 B、41 entries，SHA256 `ff8ed048f59c1c535c15c793b981ab0ad99f522b9323f935ca349e4a17f3c4bf` 与服务端 metadata 完全一致；原始记录及八份 TUnit HTML 在 `control_matrix_9910c41/`，实际执行 collector 的固定源码另存 `control_matrix_9910c41_collector.py`。后者是取回的原源文件，未做任何修改；不要用本地另有未采用收紧的建议模板充当实际执行身份。

### 7.4 本次执行身份，不能用本地草稿替代

| 执行 | Harness commit | 受测代码 |
|---|---|---|
| v1 red/green | [`a35ae087bfb3b5fb90e5e39da7439d71b3f96620`](https://github.com/SunSi12138/SharpLink/commit/a35ae087bfb3b5fb90e5e39da7439d71b3f96620) | 8ac 原始输入 + 该 commit 的严格 hash overlay；4 个新增测试。 |
| v2 red/green | [`d7db8659a2ca4287d69e5c0d7f2d8b2dafe15089`](https://github.com/SunSi12138/SharpLink/commit/d7db8659a2ca4287d69e5c0d7f2d8b2dafe15089) | 8ac + v2；恢复全局 `Open` 契约，只缩小 terminal 身份处理；4 个测试不变。 |
| H/G 固定控制 | [`9910c41fdcf4b882b42e0711f919256dff44b343`](https://github.com/SunSi12138/SharpLink/commit/9910c41fdcf4b882b42e0711f919256dff44b343) | H=完全未改 8ac；G=8ac+v2；固定 8 次完整 suite，不是直到全绿的循环。 |

可复查 [v2 overlay](https://github.com/SunSi12138/SharpLink/blob/d7db8659a2ca4287d69e5c0d7f2d8b2dafe15089/eng/issue735/historical/apply.py)、[session fixture](https://github.com/SunSi12138/SharpLink/blob/d7db8659a2ca4287d69e5c0d7f2d8b2dafe15089/eng/issue735/historical/session-tests.cs.in)、[client fixture](https://github.com/SunSi12138/SharpLink/blob/d7db8659a2ca4287d69e5c0d7f2d8b2dafe15089/eng/issue735/historical/client-tests.cs.in)。这些文件只用于封闭历史归因；不恢复 #803、不覆盖 dev、不绕开生产 Go 门槛。

## 8. TCP 慢速前进与信用死锁的证据边界

失败诊断 [run 36019812964](https://github.com/SunSi12138/SharpLink/actions/runs/36019812964) 的 `case-0.log` 保存了连续 5 秒快照：收到 DATA 数从 **8290→8595→8899→…→10728** 增加，`returned` 同步增加；窗口控制器在快照点 connectionCredit=0、128 waiters。与此同时发送 socket `Send-Q/notsent≈524 KiB`，`snd_wnd=52224` 小于 `mss=62464`，`rwnd_limited≈99.9–100%`；接收 socket 的瞬时 Recv-Q=0，bytes_received仍不断增加。

这组数据证明该次执行是**真实持续但很慢的前进**，不是所有信用永久静止的死锁；连接许可耗尽与数据已在内核发送队列相容。应用 SendPump `queuedBytes=0/pendingFrames=0` 不代表网卡/内核队列没有等待发送的数据。抓到 Recv-Q=0 也不意味着接收内核的 advertised-window行为没有限制发送。

但它不能单独证明是哪一条内核分段、延迟ACK、window-update或socket buffer策略导致了节奏，也不能证明 B3 在别的取消/terminal条件没有信用死锁。`snd_wnd<MSS` 是值得解释的关联，精确因果仍需 packet capture或匹配机制干预。原45秒超时失败保留为失败，不能延长超时后把该条转换成性能成功样本。诊断编译开关、5秒定时快照和失败收集也意味着这份数据不能并入非诊断 throughput均值。

该失败诊断固定 head 为 `13237a20b346bb1cf5f26d5bf5ffaad8334029b4`，PGO OFF、c128、128×4 KiB、connection 512 KiB、ring16、Flush16 KiB、prepared cap0；原过程退出非零并记录 `TaskCanceledException`。用其参数说明观察范围，不将它替换为增大窗口或修改 socket 后的新验收。[固定诊断代码](https://github.com/SunSi12138/SharpLink/blob/13237a20b346bb1cf5f26d5bf5ffaad8334029b4/test/SharpLink.Benchmarks/ReadyWriterStallDiagnostics.cs)

## 9. 对后续选型有效、但有范围的判断

1. **拒绝旧 #803。**真实 RPC 回退、原八轮中三轮失败和本次未改 H 的再现已经足够；不因 smoke 或某次完整 suite 通过而改为 Go。
2. **Phase A 省查表不能替代连接协调重构。**顺序 micro 的减少工作量真实存在，但 `_gate`、生命周期和异步状态机仍在；真实 RPC 结果不稳定。不同旧基线、模型和子优化百分比不能累加。
3. **B2 仍需作为独立候选审视。**有界 grant 可将小消息连接命令从 1.015625 降到 0.01953125/item，但 production writer ownership、pressure reclaim、key-only WindowUpdate、动态关闭及真实 RPC 尚未被这些模型覆盖。历史不能替新 B2 作 Go，也不能替它作 No-Go。
4. **真正 B3 必须证明整个发送路径的成本下降。**只把 controller lock 换成 ready queue，而 shared memory reservation、completion、lifecycle 和唤醒更重，无法兑现理论收益。q16 的 matched transport 改善是选取最小实验的依据，不是生产验收。
5. **本次归因 green 是因果证据，仍不满足生产交付条件。**它保留了尚未覆盖的 retired-key 窗口，以及曾发生但未解释的 decode-entry 失败；更没有新性能 A/B。本报告不声称“修好了 #803”。

更小的优化应独立评估：减少无压力路径的可避免 gate/分配、已有身份沿调用生命周期传递、取消出口责任一致，以及不改变信用 ledger 的局部成本消除。这里列的是可解释的方向，不报告未经当前 dev 真实 RPC 验证的收益数字。#840/#841 已在基线中，不能再次作为本轮成果。

## 10. 复算、原始文件与交付范围

两份标准库 Python CLI 在本次容器中实际执行并退出 0，输出与原审计的七份数值报告逐字节一致。它们只读原始证据，写入独立输出目录，拒绝 `python -O` 以免跳过原始证据校验。固定原数据的解释不会因当前仓库代码变化而改变。

```bash
python3 eng/issue735/analysis/recompute_historical.py \
  --evidence-root /absolute/extracted/evidence/historical \
  --output-dir /absolute/recomputed
python3 eng/issue735/analysis/recompute_phase_ab.py \
  --evidence-root /absolute/extracted/evidence/historical \
  --output-dir /absolute/recomputed
```

| 脚本 | 实际复核量 | 输出 |
|---|---|---|
| `recompute_historical.py` | 416 RPC records / 26 cells；768 JIT + 128 Native B3 samples；原 8 轮 stress | `production-recomputed.json/md`、`prototype-recomputed.json/md`、`stress-recomputed.json`、脚本 provenance |
| `recompute_phase_ab.py` | 1,624 microkernel rows；84 RPC records；24 c8 records / 96 results；624 Phase B rows | `phase-a-recomputed.json`、`phase-b-model-recomputed.json`、脚本 provenance |

证据档案另含：`pr*.json`、`comments*.json`、`reviews*.json`、`threads*.json`、`commits*.json`；`commits-800/` 和 `commits-803/`；固定 `source-800/`、`source-803/`、`source-803-stress/`、`source-phase-a/`、`source-phase-b/`、`source-prototype/` 及 provenance；`prototype-reconstruction.json`；历史每个引用 job 的原始日志；第 2 节所有原 ZIP 与解压内容；第 7 节新运行的 manifests、diff、build/test logs、exit codes、完整 H/G 原始 TUnit 报告。

原始 Phase A ELF 本次局部校验的精确命令如下；这是实际运行 ELF，并非只读 header/hash。退出码 0，两路 checksum 均 48,000，stdout 与 JSON 位于 `phase-a-native-local-execution.json` / `phase-a-native-local-validation.json`。它不进入任何统计性能表，也不被称为本次完整 RPC NativeAOT。

```bash
evidence/historical/phase_a/aot/bin/SharpLink.Benchmarks \
  --scenario send-no-wait --active-streams 1 --items-per-stream 1000 \
  --repetitions 3 --skip-contention \
  --output /workspace/scratch/568b0d234276/evidence/historical/phase-a-native-local-validation.json
```

本文的 No-Go 结论严格针对旧 #803 及没有达到生产验收门槛的历史证据。完整新架构是否可采用，应结合本目录其他当前 dev 实验和架构评审；没有数据的项目保持未知，不能由本报告替补为成功。
