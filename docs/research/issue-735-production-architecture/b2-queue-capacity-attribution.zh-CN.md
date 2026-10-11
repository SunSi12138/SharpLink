# 5cab：SharedMemory 发送队列容量拒绝的归因与证据边界

本次是独立只读源码和原始结果审计：没有修改生产、collector、测试或断言，没有另跑测量，没有下载由其他审计者负责的 artifacts。输入为 [run 38173700315](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315) 已保留的两个 sized DTO / JIT artifact，以及固定 `c7ba1768`、`ba9dfedc`、`5cabd4d7` Git 源码。逐文件核验记录见同目录 `queue-capacity-attribution-evidence.json`。

**结论：两个失败证明既有的有界 SendPump 拒绝了发送，不足以证明信用不守恒、泄漏、死锁或 B2 专属回归。**A 臂在没有 B2 的情况下也触发同一种失败，排除了“只有 B2 才会触发该问题”的解释。另一方面，这些失败是原负载未完成的真实验收阻碍，不能删掉、不能将预热报错换算成性能样本，也不能因为是保护性拒绝就声称该生产负载已经通过。[JIT PGO OFF 原 artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715317228)、[JIT PGO ON 原 artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715501334)

## 1. 原失败到底发生在哪里

| 运行群体 | 失败位置 | 编译臂与实际 Runtime DLL SHA256 | 原结果 |
|---|---|---|---|
| JIT PGO OFF / sized DTO | `ab-r0-sharedmemory-Server-1000x64-c128-p0-A` | A=`c7ba1768f31ce7219dc438dc6009347de6806ed5`；`32e5dbd24b06d772807c986a8cd708e694339ca7902251e22f0d8b01da244f6f` | exit 1；进程 3.5046 秒；前面 48 个测量进程成功；此处尚未进入正式 measurement。 |
| JIT PGO ON / sized DTO | `ab-r0-sharedmemory-Server-1000x64-c128-p1-B` | B=`5cabd4d7cc5ce9aa0a87424e36954e414e0912da`；`a33187fa89aacb4dd949fbb717824be5171128e8b2cf4feb649afdf735d64399` | exit 1；进程 3.8575 秒；前面 49 个测量进程成功；此处尚未进入正式 measurement。 |

原最外层异常为 `A persistent RPC worker failed before measurement.`，内层均为 `SharpLinkException: Session send queue exceeded its 8388608-byte limit (send_queue_capacity).`。堆栈是客户端 `PooledAsyncStreamDispatcher.SlowMoveNextAsync` → generated RPC wrapper → `RunWorkerAsync` 第 273 行的预热调用 → `ReadyOrFailureAsync` 第 317 行。stdout 已留下正常 codec preflight，没有 `phase=measurement_all_workers_ready`。这不是编译错误、codec 预检错误、计数器审计错误或 60 秒外部 watchdog 超时。[固定预热及 gate 代码](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs#L260-L319)

需要更正一个容易出现的口头简写：**失败进程有 CSV，但没有正式 RPC 样本。**两个 `.samples.csv` 都是 111 B 的单行表头，数据 0 行；`.fairness.csv` 都是 107 B 的表头，数据 0 行；`.workers.csv` 各有 128 行，正式 completed 总数 0，原计划 expected 总数 4096。它们不能冒充 4096 个成功样本，也不是“所有失败文件均不存在”。

预热也不是恰好 `2×128` 次 RPC。固定程序要求每个 worker 同时满足至少 2 次调用及至少 5 秒。失败时 worker CSV 记录的已完成预热 RPC 总数分别为 **738** 与 **886**；各 worker 记录的预热时间范围约为 **2.2304–2.6815 秒**、**2.5565–3.0283 秒**，均未完成预热政策。这些是预热完成计数，不可用于吞吐、CPU/RPC 或延迟对照。[固定最低预热条件](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs#L268-L280)、[失败输出保留逻辑](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs#L573-L631)

每臂均有 128 个 worker failure 文件。逐一按首行分组：JIT OFF 为 25 个容量异常、93 个 `Client is stopping`、10 个 OperationCanceledException；JIT ON 分别是 1、120、7 个。**这些二次失败数量不是独立的队列溢出次数或失败概率样本。**首个错误会驱动 harness 取消所有 worker 并 Dispose client/server，后继 transport/cancellation 异常包含主动 teardown 的影响；原日志没有足够信息把它们全部归给一个产品内部连接故障。[固定 failure teardown](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs#L237-L253)

## 2. 固定源码与实际配置

### 2.1 相同的发送核心

下列生产文件在 `c7ba1768f31ce7219dc438dc6009347de6806ed5`、`ba9dfedc3a769a14f03cb5c522f4921f5da655eb`、`5cabd4d7cc5ce9aa0a87424e36954e414e0912da` 三者中逐字节相同：

- `RpcSession.SendPump.cs`。
- `RpcSession.cs`，包含 SendPacket、异步容量等待入口及帧分类。
- `SharpLinkRuntimeOptions.cs` 与 `RpcSessionFlushPolicyState.cs`。
- `RpcSession.PreCreditTerminal.cs` 与 `ServerGeneratedBridge.cs`。

SendPump 单文件原始 SHA256 为 `3071b42cc8985078f0eb01d59f52fb6ea2f09d6298630dd3081b7ffd4e7751eb`，长度 25,092 B。运行头部的 `sendPumpSourceSha256=d294e6e7c2ffccdb47bb8c4b0907f461c9e332c650cb9ad9cfc6406551f5063a` 是**带路径/长度前缀的 source manifest digest**，不是该文件裸 SHA；本次按原 stamp 算法重算也匹配。这两个不同 hash 不能互相替代。[SendPump 固定源码](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.SendPump.cs)、[manifest digest 算法](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/stamp_identity.py#L71-L83)

每个失败位置保存的实际 Runtime DLL 已重新 hash，并与其原报告相符。A 是共同正确性控制 c7，包含三项独立正确性修复、没有 B2；B 的生产源码与 ba9 的队列核心相同，但其实际 binary 身份必须使用该次 5cab 构建，不能用 ba9 或此前 correctness 进程替代。

### 2.2 配置来源与没有现场保存的部分

| 项目 | 本负载采用的值 | 证据类型 |
|---|---:|---|
| MaxSendQueueBytes | 8,388,608 B（8 MiB） | **实际异常中的限制值**，亦符合未改的 Balanced 默认配置。 |
| StreamReceiveWindowBytes | 1,048,576 B（1 MiB） | 固定默认源码与无 override 的 builder；相同 harness 其他成功进程的 actual client options 回显。 |
| ConnectionReceiveWindowBytes | 16,777,216 B（16 MiB） | 同上；协商取双方上限，不由 B2 quantum 更改。 |
| MaxPreCreditSerializedBytes | 4,194,304 B（4 MiB） | 固定默认源码；这里只适用于相应 unsized 等待路径。 |
| Balanced flush | 16 KiB threshold；queue 空时 flush；未设置显式时间批次 | 固定 profile/default 路径；没有为了 A/B 改 flush。 |
| codec / compression | generated sized DTO；无压缩 provider | 失败进程原 codec preflight / identity。 |

固定 `EvidenceEnvironment.CreateAsync` 只建立同样的 TCP/SharedMemory 传输、服务和既有 timeout/heartbeat 政策，没有设置 flow windows、send queue 或 flush override；默认 runtime profile 为 Balanced。客户端与服务端均由该路径构建。[harness builder](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/EvidenceEnvironment.cs#L45-L88)、[默认资源值](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/SharpLinkRuntimeOptions.cs#L19-L86)、[profile 对队列的默认选择](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/SharpLinkRuntimeOptions.cs#L200-L239)、[默认 Flush](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSessionFlushPolicyState.cs#L38-L73)

**边界：失败进程没有成功走到 BuildReport，因此没有该进程的 actual window/queue support snapshot，也没有 settled snapshot。**16/1 MiB 是经固定源码和相同配置路径支持的配置结论，不能写成“在失败瞬间读取的信用余额”。成功的相邻进程提供相同 client options 回显，不等于失败进程的现场状态。[成功才输出这些值的位置](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs#L547-L569)

## 3. 为什么信用充足仍可能被队列拒绝

### 3.1 两个不同资源账户

此处 item 64 B 是**包含 codec framing 的实际序列化长度**，不是原字符串长度或一个未核实的逻辑 payload 参数。失败进程的 generated codec 预检记录：

```text
actualItemCanExactSize=true
actualEncodedItemBytes=64
actualDtoTextCharacters=27
actualUncompressedStreamDataFrameBytes=81
```

81 B = 15 B Protocol v2 header + 2 B stream ID + 64 B item。预检还验证 sized serialization 与普通 serialization 字节完全相同并完整 round-trip，因此无需估猜 framing。[DTO 定义与尺寸预测](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/GrantBenchmarkContracts.cs#L29-L49)、[真实 codec preflight](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/PayloadEvidence.cs#L14-L27)

对这类普通、未 oversized 的 item，信用账户按 item 字节变化：

- A：`Wc = F + ΣDi`，每流 `Ws = Ci + Di`。
- B2：`Wc = F + Σ(Gi + Di)`，每流仍为 `Ws = Ci + Di`。refill 先从 F 转入 Gi，实际 item 再由 Gi 转入 Di。
- SendPump 独立按完整 frame 长度预留 Q。Q 包括已经预留但尚未入 Channel 的帧、Channel 内帧，以及 pump 的 pending/Flush batch，直到相应 Release 才扣除。**Q 不等于 ΣDi，也不是 pool 实际容量或进程堆内存。**

B2 的 grant 只是连接信用的有界子分配；它没有为普通 item 预留 SendPump queue bytes，也没有把 _sendGrantPressure 连接信用压力改成队列容量压力。[B2 grant 守恒与 refill](https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L85-L119)、[队列预留实现](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.SendPump.cs#L401-L431)

### 3.2 普通数据可用的容量比异常消息的 8 MiB 更小

既有 SendPump 会为真正进度类帧留出容量：

```text
progressReserve = clamp(8,388,608 / 512, 4,096, 65,536) = 16,384 B
normalLimit = 8,388,608 - 16,384 = 8,372,224 B
```

分类实际只有 Ping、Pong、WindowUpdate、GoAway 属于 progress；StreamData、Request/Response、StreamComplete（含 error）、Cancel 都在 normal 类。这是保持 Request→Cancel 和 DATA→Complete 顺序的现有设计，不能凭“取消是进度动作”便声称 Cancel 使用了保留队列。[reserve 计算](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.SendPump.cs#L73-L87)、[实际分类](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.cs#L367-L384)

对本负载的小 normal frame，TryReserve 在 `current <= normalLimit - frame.Length` 时才 CAS 增加 Q；条件不满足就返回 Full。因此异常文案里的 “exceeded ... limit” **不表示已经把总 Q 写到 8 MiB 以上**。它通常表示下一帧不能获得保留，保护发生在入队前。若现场被拒的恰好是一个 81 B DATA，源码条件意味着当次读取 Q 至少为 8,372,144 B；但日志没有原始服务端 frame type/length，故这个数只能作为条件推导，不能当成实测峰值。[Full 前的预留条件](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.SendPump.cs#L108-L134)

### 3.3 一个解释可行性的容量模型，不是原运行轨迹

假设同一时刻 128 个流各有一轮 1000 个 64 B item 待发送，并假设这批数据在生产期间没有得到足够的 Flush/release：

| 数量 | 计算值 | 与边界的关系 |
|---|---:|---|
| 每流 item 信用需求 | 1000×64 = 64,000 B | 小于 1 MiB 流窗口。 |
| 一轮全部 item 信用需求 | 128×1000×64 = 8,192,000 B | 小于 16 MiB 连接窗口。 |
| 同一轮全部 DATA frame bytes | 128×1000×81 = **10,368,000 B** | 大于 8,372,224 B normal queue 可用容量。 |
| 差值 | 10,368,000 − 8,372,224 = 1,995,776 B | 说明仅凭信用账户并不能保证整轮 frame 同时入队。 |

由此可以解释**在信用守恒完全正确时，仍可能先遇到本地队列容量拒绝**。同一发送方向的 Complete、其他 normal/control 帧及已在途 batch 还会占用队列；表中没有把这些计算进去。客户端 Request 使用另一发送方向的 SendPump，不能额外加到服务端 DATA 的 Q 上。

这不是“原测试确实同时积压了 10,368,000 B”的证据。实际 harness 是一个物理连接、128 个 closed-loop worker，生产、读取、WindowUpdate 和 flush 并发发生，而且失败前已完成 738/886 次预热 RPC，远不是只有一轮固定 burst。原结果明确没有 queue peak 仪表，也没有 Flush pending 时间线；无法从总潜在输出量倒推出失败瞬间的队列组成、峰值、reader 吞吐或 credit debt。原文件中成功报告的 `queueDepthPeak=not-instrumented` 也不允许补值。[closed-loop worker 与一次性测量 gate](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs#L138-L187)、[明确未测的指标](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/Program.cs#L565-L569)

## 4. known-size bridge 进入 Full 的链路和所有权

### 4.1 A 与 B 的共同后半段

A 的路径是：generated producer→TryGetEncodedSize→AcquireStreamSendCreditAsync→SerializeSized/ReleaseSnapshot→SendPacket→SendPump.TryEnqueue。B 只在前半段增加 immutable grant lease 首次选择及 refill/local debit；**两者的最后一次 SendPacket 都是同步 TryEnqueue，队列满时不会在这个入口异步等容量**。[A known-size helper](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L265-L352)、[B2 helper](https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L309-L405)、[SendPacket](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.cs#L181-L212)

这里无需假设一个新的隐藏准备队列：精确大小路径在获得信用后才序列化；4 MiB pre-credit serialized budget 主要约束需要等待 flow credit 的 unsized 预序列化路径，不承担这个 sized 快路径的 SendPump admission。生产者的 MoveNext 与 credit acquire 都可能同步完成，`await` 一个已完成 ValueTask 不构成强制调度让出；该事实只说明快速生产 burst 是可能的，不证明本次 writer 一定被 producer 饿死。[generated 即时 producer](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/GrantBenchmarkService.cs#L52-L63)、[unsized budget 的条件入口](https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs#L82-L128)

### 4.2 Full 分支应由谁负责清理

| 资源 / 状态 | 既有源码的责任人 | 此次日志能证明的范围 |
|---|---|---|
| 被拒 frame 的 buffer | helper 先把 `ownsWriter=false` 交给 SendPacket；TryEnqueue 因 TryReserve 失败调用 ReturnUnreserved→buffer pool.Return。 | Full 的源码路径有唯一接管方；没有现场 pool owner/return 计数，不能宣称全部原运行 buffer 均已逐一核验。 |
| 被拒 item 的 64 B flow credit | known-size helper catch：A 调 ReturnUnsentStreamCredit；B 调原 generation 的 lease.ReturnUnsent。 | 未入队 item 有明确退款路径；原失败现场没有 F/D/G snapshot，故不能把源码路径直接当成一次实测守恒证明。 |
| 被拒 frame 的 SendPump queue bytes | TryReserve 没成功则未计入 Q，ReturnUnreserved 不减 Q；避免给从未预留的字节二次退款。 | 原 Full 异常与此保护分支相符；不是 Q 已越界的测量。 |
| 已接受/已经进入 pending 或 PipeWriter 的 frame | pump 独占；Flush 完成后 ReleaseBatch，异常/teardown 在 RunAsync finally 释放 pending 并 drain 两队列。 | helper 不把这些已接收帧当“未发送”再次退款；实际 Flush 进度及最终 Q=0 本次未记录。 |
| snapshot | SerializeSized 成功后先 detach 再 Release；异常 cleanup 保持独立 snapshot 修复。 | codec preflight 已通过；没有此次全部 stream snapshot 的最终计数。 |
| B2 unused Gi | outer finally 在用户 enumerator Dispose 前调用 grantLease.Dispose；其只还 unused Gi，不代替已 admitted item 退款。 | 固定实现已具备清理路径；此次失败进程没有 grant-settled 指标，不能量化这次暖机的真实 grant 使用或最终余额。 |

[buffer/Q 完成路径](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.SendPump.cs#L462-L515)、[Flush 后释放和失败 drain](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.SendPump.cs#L268-L341)、[B2 精确退款与终结](https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L245-L326)、[Dispose 的清理边界](https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L281-L302)

共同控制和 B2 都保留 `CreditRefundedByAbort` 区分“错误终结”与“abort 已全部退款”；因此不能再次把 Complete(error) 当作已经退还所有在途信用。#840 的 stream/connection 耦合 WindowUpdate 不在这次队列拒绝中被改写。队列 Full 与 WindowUpdate 是不同状态转移，只有现场余额或独立 race 能证明一个破坏了另一个。

现有确定性测试 `RejectedUnsizedPacketAfterCreditHandoffMustReturnItsBudgetWriterAndExactLeaseCredit` 故意构造 32 KiB queue / 4 KiB progress reserve，使 30 KiB DATA 必然无法入队，验证 ResourceExhausted、exact credit/budget/writer 归还、连接仍活及终结退休。该测试在本次 5cab focused 中实际 pass。**它是较小、unsized fallback 的确定性证据，不能冒充本次 8 MiB、128 流、全部 sized 的失败现场清理证明。**[限定测试](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.UnitTests/Runtime/B2GeneratedStreamBridgeTests.cs#L144-L184)、[实际 correctness artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715271556)

### 4.3 资源拒绝、连接关闭与信用死锁不是同一件事

Full 分支构造 ResourceExhausted 并返回/抛出，没有在该分支直接调用 session.Fault。ServerGeneratedBridge 可以把异常映射成 StreamComplete(Error)，客户端首个错误确实携带原容量 message。但是 StreamComplete(Error) 本身仍在 normal queue，不能由这条设计路径推断它在所有饱和交错下都能成功发出；本次没有保留所有终结帧入队及连接状态时间线。[错误映射及发送](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Server/ServerGeneratedBridge.cs#L77-L111)、[error terminal 的入队与退休](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.PreCreditTerminal.cs#L5-L46)

另一方面，harness 首错后主动关闭环境，故随后大量 `Client is stopping` 或取消不能证明产品因为单一流错误无故终止整个连接。正常完成的输出也未产生，因而不能反向宣称连接一直健康、所有信用和 buffer 均已结清。

真正的信用死锁需要证明系统有合法可前进动作却因信用/等待依赖环无法推进；此处已观测到的是**数秒内明确拒绝并退出**，不是永久等待。未测 Flush 阻塞与 queue backlog 原因也不能被笼统归为“网络慢”——该失败发生在 SharedMemory，本次没有与 TCP advertised window/MSS/Recv-Q 对应的网络证据。

## 5. 已证实、合理假设与尚未测量

| 类别 | 结论 | 证据或缺口 |
|---|---|---|
| 已证实 | 两个失败都在真实 generated RPC 预热，原 exit1、正式样本0。 | stdout、stderr、空样本 CSV、128-row worker CSV、原程序行号。 |
| 已证实 | A 无 B2，也能出现相同容量拒绝。 | JIT OFF A 的 compiledMode、Runtime DLL、固定 c7 源码。 |
| 已证实 | 共用 SendPump 在 Full 时拒绝下一帧；不能据文案断言已经超过硬上限。 | TryReserve 的 pre-CAS 条件及 ReturnUnreserved。 |
| 已证实 | item credit 与 full-frame queue 使用不同单位，16 MiB 信用不保证早于 8 MiB queue 形成背压。 | item64/frame81 预检、options、同一发送链。 |
| 合理假设 | 高并发即时 producer 可能比 writer/reader 的 Flush/release 更快填充普通队列。 | producer 同步循环和 SendPacket TryEnqueue 允许这种时间关系；没有该次时间线。 |
| 合理假设 | B2 降低连接协调可能改变 burst 时序和 queue-full 概率。 | B2 只改 credit authority 路径；JIT ON 的 A 一次通过而 B 下一次失败；不足以排除顺序、运行时和调度变化。 |
| 尚未测量 | 被拒的原始 frame type/length、Q、pending bytes、实际 queue peak、Flush blocked 时长及 reader 进度。 | 原 instrumentation 不含这些值。 |
| 尚未测量 | 失败 teardown 后每流 F/C/G/D、pool buffer 唯一归还、grant settlement、所有 terminal 的发出情况。 | failed 路径没有 BuildReport/settled snapshot。 |
| 不能下的结论 | B2 专属根因、B2 比 A 更容易失败的概率、发生内存泄漏或信用死锁、本负载可以生产验收通过。 | 缺少同群体完整匹配样本及现场资源账本；A 的独立失败反对专属归因。 |

JIT OFF 的 A 失败与 JIT ON 的 B 失败不能拼成一个同运行时的 A/B 对照，更不能给失败位置插入零吞吐再算平均。JIT ON 的先前 A p0 是一次真实成功，但其后的 B p1 失败只构成不完整 pair；它保留为失败证据，不产生该 cell 的合法 B/A 性能比。

## 6. 最小诊断和止损含义

### 6.1 现在即可下的决定

这两个 failure cell 保持 Failed，后面的既定位置保持原 NotRun。该轮不能声称完整 RPC matrix 或生产验收通过。继续分析已完成 cell 时必须保留群体/缺失结构，不能把这个 SharedMemory 高并发长流 cell 静默剔除。

**不增加窗口、不增加 SendQueueBytes、不改变 socket/传输参数、不降低并发或 item 数，不靠更改 warmup/timeout 让它通过。**这些改动会改变任务要求的边界或匹配负载，不能用于拯救本轮收益。

单凭本问题不能推出所有 B2 都应 No-Go；它揭示的是当前共用发送入口的既有容量语义与所要求负载之间的边界。对本次生产接入，应先停止“已通过全部资源/性能验收”的主张。只有同样配置与负载的真实 RPC 有可重复收益、且全部必需正确性门槛成立，才值得支付新的生产接入复杂度。

### 6.2 如果确有继续投入的证据，最小下一步应是什么

最小诊断应是有界、预声明的**正确性/资源现场捕获**，不把它混入既有性能样本：

1. 在首次容量拒绝处捕获同一时刻的 normal/progress frame type、长度、Q、effective normal limit、pump pending/Flush 状态及选中流的 F/C/G/D/epoch；保存完整首次异常和异常传播路径。
2. teardown 后捕获 queue bytes、prepared budget、grant/credit 余额和 owner/buffer 计数；从单一确定性 Full 夹具检查已接管 frame 与未入队 frame 的责任边界，以及 error terminal 也遇到容量压力的路径。
3. 如需真实负载诊断，固定原配置、原负载及事前 A/B 顺序/次数；保留全部 red 和未完成位置，不提高门槛、不进行自动重试到绿。诊断样本另立群体。
4. 只在这些证据确认了一个可修复的根因后评估更改，不先把“完整矩阵跑不完”归咎于测试。

本任务此次**没有实施或执行上述新诊断**。已有原材料能支持的结论已在前文列明。

### 6.3 为什么不能立刻将 SendPacket 改成异步等容量

现有生产已提供 `SendPacketWithBackpressureAsync`、`SendPacketAsync(waitForCapacity: true)` 与 pump.ReserveAsync。它们在 Full 时保留 frame owner、等待容量变化，并涉及取消、TCS、跨队列交接；这不是一个“缺少 API 所以只好抛错”的问题。[既有 backpressure 入口](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.cs#L291-L338)、[容量等待](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/src/SharpLink.Runtime/RpcSession.SendPump.cs#L434-L459)

但把 generated known-size helper 接到它会改变“信用已授予、完整 writer 已准备、队列尚未接收”之间的等待期及失败责任。必须明确等待时占用的 flow credit、准备 buffer 的有界数量、首错/取消后退款、writer 已接管后的不可撤销范围，以及 terminal 和 Flush 阻塞下的公平性。它可能让同一负载成功等待，也可能增加等待者、调度和尾延迟；不能只因为已有 helper 就绕过生产验证。

因此，没有真实 RPC 的收益信号和单独清晰的资源归因前，**不为救本轮 B2 大改 async backpressure**。如果资源接入策略本身值得改善，应作为与 B2 分开的基线行为问题研究，让性能比较双方共享经独立验证的实现，不把它的收益记成 B2 grant 收益。

## 7. 原始材料索引与本次审计范围

| 原 artifact | ZIP bytes / SHA256 | 本地目录 |
|---|---|---|
| [11715317228：JIT PGO OFF sized](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715317228) | 17,847,587 B；`a2ebc9b8c200abe2c9644f930066b68842f2659844f8d5d61a18ed75bd5cbd42` | `jit-pgo0-sized-dto/raw/` |
| [11715501334：JIT PGO ON sized](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715501334) | 17,884,611 B；`e6b9d11213c7baa1694effc388252b26243689572bc47b680fba5ae2a43f7a27` | `jit-pgo1-sized-dto/raw/` |

两 ZIP 由负责原样本审计的另一工作者保存并验证。本次独立重新计算本地 ZIP 的大小/digest，并将每个群体中 **139 个与失败相关的原文件**（含 128 个 worker exception、报告/CSV、stdout/stderr、plan/exits 和对应 Runtime/benchmark binary）逐字节与原 ZIP entry 对比，全部相同；没有重新下载，没有修改原文件。

可从每个目录的 `samples/<上文 stem>.txt`、`.failure.txt`、`.samples.csv`、`.workers.csv`、`.fairness.csv`、根目录 stdout/stderr 与 `exits.json` 重看原证据。这个报告不重新汇总已成功的全部性能样本；其完整 sample 审计由 `strict-recomputed.json` 和相邻独立结果负责，避免在此混合不同测量群体。
