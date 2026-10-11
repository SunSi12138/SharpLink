# Issue #735：B2 服务端 grant pilot 的生产架构与验收边界

## 1. 固定范围与当前结论

本 pilot 把[候选评审中的方案 A](frameworks-and-candidates.zh-CN.md#11-方案-a改进-b2有界-stream-local-grant)缩成一个可否证的生产接入：复用现有 `SendState` 与 SendPump，为符合条件的生成式服务端输出流捕获一次 immutable-generation lease，后续 item 尝试消费有界 stream-local grant。authority 仍是同步临界区，不是新增 EventLoop、worker 或信用命令队列。它没有实现方案 B 的 Ready Writer，也没有方案 C 的双模式 writer 切换；B3 研究原型的量子收益不能记到这里。

| 身份 | 固定提交与含义 |
|---|---|
| 原始 dev | `f8ba89121c87a6410a0139c44d7db30eab877f41`；含 #840 / #841 |
| 本轮共同正确性控制 A | [`c7ba1768f31ce7219dc438dc6009347de6806ed5`](https://github.com/SunSi12138/SharpLink/commit/c7ba1768f31ce7219dc438dc6009347de6806ed5)；双方共同包含 #849 / #850 / #851 的独立正确性修复，不包含 D / B2 |
| 经审阅的 B2 生产树 | [`ba9dfedc3a769a14f03cb5c522f4921f5da655eb`](https://github.com/SunSi12138/SharpLink/commit/ba9dfedc3a769a14f03cb5c522f4921f5da655eb)；本文件的源码行号均固定此提交 |
| 正确性验证提交 | [`754d723c7866a67a48f3952495d8aeda3159d07b`](https://github.com/SunSi12138/SharpLink/commit/754d723c7866a67a48f3952495d8aeda3159d07b)；仅格式化测试中的 Thread initializer，生产字节与 ba9 相同 |
| 首轮测量 recipe | [`608288167436ce361920e7ee41fa9bb958229707`](https://github.com/SunSi12138/SharpLink/commit/608288167436ce361920e7ee41fa9bb958229707)；生产字节仍为 ba9，新增同 SHA gate 后的独立测量 |
| 后续测量修正 | [`5cabd4d7cc5ce9aa0a87424e36954e414e0912da`](https://github.com/SunSi12138/SharpLink/commit/5cabd4d7cc5ce9aa0a87424e36954e414e0912da)；仅调整新 RPC harness 的 nullable 注解，完整生产树仍为 ba9；其 footprint 与 RPC 原样本均已独立复核；最终 pilot No-Go，见配套结果报告 |

相对共同控制，生产差异仅以下四个文件；其余 `src`、协议、公共 API、默认窗口、内存限制与 SendPump 均相同。不能把 f8→c7 的正确性变化解释为 B2 优化收益。

| 文件 | ba9 SHA256 |
|---|---|
| [StreamFlowController.cs][controller] | `8f40d07367532bea3890c3a8015c29fc911ff7a49f349b1bfe5271ca30f1cd24` |
| [StreamFlowController.SendGrants.cs][grants] | `df37087330c876d1ad91bc87966c983d2d633d5ca5fbead662c3097362f2071d` |
| [RpcSession.GeneratedServerBridge.cs][bridge] | `2604d69b91c0d67f62b71ad54ecb500c75dbdf66ed5c9e3dc2d7d9f4353ed396` |
| [RpcSession.PreCreditStreaming.cs][unsized] | `57d829c047ccf2098fd3789c69fb5d87d4fe9e73adcaff23a3e12a4ab190ecba` |

**证据冻结点：**754 的 [run 38172137769][correctness-run] 已完成 25 个预声明步骤：41 focused、四轮 2,123 unit、271 generator、77 load、454 integration 与既有工程门槛通过；指定 DisableGrant mutant 的 1 fail / exit 2 是被保留的预期 red，其 Dispose 控制仍通过。[原始 artifact][correctness-artifact] 已核验 83 项内部 manifest、1,592 项固定 Git 源码与 33 项二进制。此前 [d472 的实际桥接 40/41 red][deadline-red] 仍保留。608 的 [run 38172711056][footprint-run] 中同 SHA gate 与 footprint job 实际成功，六个 RPC job 失败，整体仍为 failure；第 7 节仅报告已经独立核验的分配附录。**本文件记录架构与分配边界；随后完成的真实 RPC 及最终阶段 No-Go 见 [配套结果报告](b2-real-rpc-methods-and-results.zh-CN.md)，不把本附录分配当作 RPC 性能。**

## 2. 唯一额度 authority 与守恒

连接存活且账本有效时，令 `Wc/Ws` 为协商窗口，`F` 为 `_sendConnectionCredit`，`Ci` 为 `state.Credit`，`Gi` 为 `UnusedGrant`。定义证明变量 `Di = Ws − Ci`，表示所有**尚未结算的已授信债务**，包括 helper 已 admission、尚未完成序列化或发布的 item，以及 SendPump 接手／在途 item；它不是“已上网字节”的别名。无需新增逐 item 的 R/S 表即可写成：

```text
Wc = F + Σ(Gi + Di)       Ws = Ci + Di
Gi ≥ 0, Di ≥ 0           Di = 未发布但已授信债务 + 已交接且未结算债务
```

| 事件 | 同步协议内的净转移 |
|---|---|
| 第一次成功 capture | exact reserve：`F−=n, Ci−=n, Di+=n`，`Gi=0`；没有首 item 投机 grant |
| refill 并授信当前 n | 先回收本流旧 Gi；`g=min(Q,Ci,F)`，`F−=g, Gi=g−n, Ci−=n, Di+=n`；Q 包含当前 item |
| local item | `Gi−=n, Ci−=n, Di+=n`，F 不变；领取 Gi 时没有预扣 Ci |
| 大 item / disabled / FIFO admission | 原 exact reserve；`n>Q` 不附赠 grant；oversized 仍须满足原双层 full-window 借用条件 |
| writer 接受 | 未发布债务变为已交接债务；F、Ci、Gi、Di 总额均不改变 |
| 未发送退款 n | 在合法 owner 上 `F+=n, Ci+=n, Di−=n`；不能归还已交接或已 full-abort 结算的债务 |
| WindowUpdate u | `δ=min(u,Ws−Ci)`，`F+=δ, Ci+=δ, Di−=δ`；保留 #840 的有效 delta 公式，Gi 不参与 stream debt |
| 回收 idle Gi | `F+=Gi, Gi=0`，Ci / Di 不变 |

以上转移对应 [first capture / refill][capture]、[local][local]、[WU / refund][returns] 和[原 oversized 条件][oversized]。只有原 oversized 规则可使相应 F 或 Ci 暂时为负；Gi 自身不借用、不为负。证明假定合法 peer 按已消费的已发布数据发 WU；Protocol v2 没有新增 generation/sequence，#840 的当前债务裁剪不构成逐 frame 或跨 generation 的网络重复更新识别器。

F、字典、waiter 与生命周期变更由同一 `_gate` authority 串行化；Gi/Ci 的局部原子转移由 `state.Gate` 保护。所有同时需要两者的路径按 **`_gate → 一个 state.Gate`**，不同时持有两个 state gate。local miss 与 Dispose 的第一次 close 都先释放 state gate，再进入 authority。执行线程是当前调用者，故不能称整个 controller 无锁。[锁包装与字段][state-fields]、[refill / recall][recall]。

## 3. 静态 hot path 成本：只数源码操作

下表把一次显式 owner-lock entry 记为 O，一次非空 state-lock entry 记为 L；Sg 是本次扫描中拥有 lazy Gate 的状态数，M 是原信用 waiter 数。计数针对未取消／未终止的指定路径；不包含 `Lock.Enter`、Channel、TCS、pool 内部的机器级 CAS，也不是耗时、分配字节或反汇编测量。表中 admission 后的 SendPump 成本还要另外支付。

| 路径与前提 | 信用部分的锁、查表和队列 | 新对象／显式操作入口 |
|---|---|---|
| 既有 live state 的首次成功 capture，无压力 | 1 O + 1 L；1 次 TryGetValue；无信用排队 | 1 个新 context；lazy Gate 尚无时 1 个新 Lock；1 次全局 Activated Increment；F/Ci 两次写入 |
| 新 key / pool rent | 在上行之外执行 Rent/Add；已带 Gate 的 pooled state 租用另有 1 L | 未命中 pool 才 new SendState；字典新增及容量扩展按既有实现发生，不谎称首流零分配 |
| Gi / Ci 充足的 local item | 0 O + 1 L；0 查表、0 信用命令／入队 | 0 显式 new / TCS / Interlocked；两个显式 Volatile.Read（connection terminal、pressure）；Gi/Ci/LocalItems 三次普通写 |
| local miss，F 充足的正常 refill | 合计 1 O + 2 L；0 查表、0 信用入队 | F/Gi/Ci/hint/OwnerRefills 五个写入口；若旧 Gi 非零先额外回收；返回 Completed ValueTask |
| disabled 或 `n>Q` 的 exact 成功 | 合计 1 O + 2 L；无信用入队 | F/Ci/ExactSlowItems 三个写入口；没有 grant 摊销收益 |
| local miss、无既有 waiter、F 不足、owner 主动回收后重试 | 合计 1 O + `Sg+3` L（执行完整 scan 时）；遍历已有 Values | 两次 pressure Volatile.Write；每个非零 Gi 三个余额／计数写入口；无新增 recall 命令或等待 idle producer |
| contended 仍不能授信，需要入队 | 在此前失败 probe 外，再有 1 O + 2 L、1 次查表，随后 AdmitWaiters 的 scan 成本 | new CreditWaiter、字段初始化 new TCS、AddLast(value)、WaitAsync 的取消注册／异步 Task 路径；ready List 按需创建扩容，不能把这些合成“一个对象” |
| WU / 合法 ReturnUnsent，命中 state | 1 O + 1 L、1 次查表；可能 retire/remove；随后 AdmitWaiters | 正常无 waiter 时无显式新对象；Dispose 前 refund 记本地计数，Dispose 后**成功** late refund 另有 1 次全局 Add |
| DisableGrant | 1 O + 1 L、1 次查表；随后 AdmitWaiters | 禁止未来 grant、回收未用 Gi；不关闭 lease、不筛除其原等待者 |
| 首次 Dispose | close 的 1 L，释放后再 1 O + 1 L；1 次查表，扫描 M 并只拒绝该 context 未授信 waiter | 发布一次证据：1 Increment + 5 Add；存在 rejected/ready 才分配相应 List；重复 Dispose 仅 1 L 即返 |
| CompleteSendStream / request Abort | Complete 命中时 1 O + 1 L；Abort 1 O 下扫描全部 S、锁定匹配 state；均处理原 M waiter | Complete 留债务 tombstone；Abort 回收 Gi 与当前 Di；按需列表，TCS 完成在锁外 |
| connection Complete | 1 O，下游逐 state close / recall；还清理原 receive 状态 | 显式 new CreditWaiter[M]，清字典与 pool；锁外完成既有 TCS，连接账本随终止失效 |

逐项出处：[capture][capture]、[local / lease admission][local]、[refill / recall][recall]、[contended][contended]、[waiter / TCS][waiter]、[WU / refund][returns]、[Dispose / metrics][lease-close]、[stream terminal][terminal]、[connection terminal][connection-close]。首次失败 capture 不 reserve 当前 n；信用不足的新 key 不发布 state，但已有 legacy state 的 capture 可以先创建 lazy Lock 后失败，不能概括为所有失败 probe 都零分配。

`AdmitWaiters` 在 M>0 时先发布压力并回收可用 Gi，随后沿原 FIFO 查表／检查：connection-credit 阻塞的头部停止本轮；仅 stream-credit 阻塞者可被跳过，让其他可发送流前进；有 waiter 时不授新 grant。[实现][admit]。一次压力服务最坏为 **O(S+M)**，多次补充、退款、终态可反复触发；bool `_mayHaveUnusedSendGrants` 只跳过明确无需扫描的情况，不提供永久 O(1) 保证。它不是第二份连接额度台账。

**继承基线的组合 HOL 缺口（静态可达，尚未执行新 fixture）：**`Ws=8,Wc=16` 时，先让 s1 占用 8、s3 占用 4，则 `F=4,C1=0`。head 为 s1 的 8 B waiter，后面 s2 的 4 B waiter 在双层窗口都可发送。现有 AdmitWaiters 先判断 head 的连接额度并 break，尚未进入 stream-credit 不足时的 skip，于是尾部也被挡住；有 waiter 又禁止直接 reserve 绕过。该判断顺序在 [f8 基线](https://github.com/SunSi12138/SharpLink/blob/f8ba89121c87a6410a0139c44d7db30eab877f41/src/SharpLink.Runtime/StreamFlowController.cs#L724-L773)、c7 和 [ba9 pilot][admit] 中一致。既有 stream-blocked 测试的 head 只需 1 B、F=24，未覆盖这个“双不足 head + 可用 tail”的组合。

这是具体源码条件可达的公平性反例，不是本轮已执行的确定性 red，也不是 B2 新增的已实测死锁。是否调整 stream/connection 的 eligibility 检查次序，仍需针对 FIFO、oversized 部分偿还、取消和 #840 计账运行独立 red/green；本轮 No-Go 没有为继续优化而扩展这一改动。因此不能把前面的路径描述或 41 个 focused 测试解释成完整公平性验收。

每个已发布 DATA 仍经过一次 SendPump 字节预算的 `TryReserve`、一次正常队列 `TryWrite` 和一次 `Signal`；真实唤醒次数不等于 Signal 调用次数，底层锁／原子、队列 segment、Flush batch 和保留预算均仍存在。[SendPacket][send-packet]、[SendPump enqueue][pump-enqueue]。B2 没有 Ready Writer 队列，也没有减少 DATA 的 writer handoff；它只尝试减少**信用 controller** 的逐 item 连接级临界区。

## 4. stream、context 与 frame 的生命周期

| 事件 | context / SendState | 债务和 buffer owner |
|---|---|---|
| capture | 一个 generation 最多一个 CurrentLease；context 保存 owner/state/key/epoch | 首 n 已授信，交给原 helper，后续不能再 reserve 一遍 |
| Disable | `GrantEnabled=false`，lease / generation 仍有效 | 归还 Gi；后续 exact 等待、退款与 Complete 仍使用同一 lease |
| Dispose | 先发布 Closed，再进入 owner 回收 Gi；CurrentLease 留在同一 generation | 不撤销已 admission 的 n；只拒绝仍在队列的本 context waiter，保留同 key legacy waiter |
| Complete(error 或 clean) | 保存首个 state error、close context、减少 active；有 Di 则保留 tombstone | 只回收 Gi，不凭 error 的存在宣称 Di 已退款 |
| request full Abort | context 保存 terminal；Gi 与当前 Di 实际归还，`CreditRefundedByAbort=true` | 晚到 ReturnUnsent 忽略；物理 writer / snapshot 清理仍由原 owner 完成 |
| 正常退役并 rent | `Completed && Ci==Ws && Gi==0` 才入池；rent 在 state gate 下清字段、换 epoch / CurrentLease | 老 context 不回池；三重 identity/epoch/current-reference 检查阻止其操作新 generation |

出处：[state / lease][state-fields]、[epoch 检查][lease-identity]、[池化 rent/return][pool]、[terminal][terminal]。池满时不再保留新的 idle state；Abort 对已完成匹配状态沿原删除路径处理，不能把“所有退役都回到池”当成契约。

**#851 边界：**helper 已授信后可能在非合作 serializer 中停留；deadline 先结束 pump，服务端 owner 随后 `CompleteSendStream(error)`，helper 才退出并退款。`AbortException!=null` 只说明终态，不能说明 n 已被结算；ba9 使用只在 full Abort 实际结算时设置的 `CreditRefundedByAbort`。Dispose 后的同 generation 合法 `ReturnUnsent(n)` 因此仍可结清 tombstone；重复超额退款断言、旧 lease 拒绝和连接终止短路保留。[helper catch/finally][sized-helper]、[server owner][server-owner]、[refund / full Abort][returns]、[terminal][terminal]。

正常 pump 顺序为每 item await admission/helper，EOF 后发布 StreamComplete，finally 先取消 lifetime、立即 Dispose lease，然后才等待／观察 enumerator Dispose。[pump lifecycle][bridge-pump]。**已 admission 的 ValueTask 直接交回 helper，不在完成后补一个 Closed/token throw**，否则会制造“额度已扣却没有 helper 接手”的空洞。[AcquireAsync][local]。成功 SendPacket 后原队列拥有 frame；helper 只清理尚未成功发布的失败，session 负责被拒收 frame 的归还，不能把部分写出的数据作为 unsent。[publication][send-packet]、[sized helper][sized-helper]。

异常优先级也有准确范围：入口先做长度与 token 检查；进入 lease 的 `GetFailure` 后，依次是该 context 已选定的原 terminal、connection terminal、Closed/stale 的 stream-closed，最后当前 state 的 abort/completed。`Close(null)` 不覆盖已有 terminal；后到的有内容 terminal 可补入尚空的 context。[实现][lease-identity]。不能据此声称同时到达的用户取消与远端异常存在一个新的全局优先顺序。snapshot 的一次释放及原操作异常保护复用共同修复；第三方 pool Return 自身抛错等更宽异常边界不由此表自动证明。

## 5. 资源上界及仍须测量的成本

令 K 为协商 stream 上限（默认 1,024），S 为当前字典状态数，P 为 idle pool 数。仍有 `S≤K`（active+tombstone 合计），`P≤min(K,128)`；没有第二张 per-stream 表。普通 grant `0≤Gi<Q`，Q 固定 4,096 且每 item n≥1，所以 `ΣGi≤min(Wc,S×4095)`；这是被移交的**信用**，不是同样大小的新缓冲区。[原上限/构造][limits]、[grant][capture]、[pool][pool]。

新增逻辑常驻成员：controller 的 Q、pressure、scan hint；每 SendState 的 Gate 引用、UnusedGrant、CurrentLease；每既有 CreditWaiter 的可空 GrantLease 引用；每成功捕获 generation 一个不回池 context，含稳定 terminal/closed、owner/state/key/epoch 与五项局部证据计数。Gate 仅首次需要时创建，保留跨 pool rent；静态全局证据只有计数，不持有 context。字段估算本身不能证明对象头、对齐或物理字节；第 7 节另外给出已经执行的调用分配差值，仍不等同于 retained heap。[字段][state-fields]、[metrics][lease-close]、[waiter][waiter]。

CurrentLease 在 Dispose、正常入池后仍指向已关闭 context，下一次 rent 才清除；controller 直接保留的 current/pool context 数量可由上述 S+P 界定。**全部旧 generation 的存活数不能仅凭 128 pool 推导硬上界**：未结算 helper 或其他合法引用可能持有旧 context、state 和 owner。循环引用本身可 GC，但外部根及非合作 producer 的存活期限须另验；DisposedStreams 计数完成不等于 buffer、snapshot 或旧引用全部回收。

一条新风险是 **pool 的使用历史影响 legacy CPU**：从未启用 B2 的 state 没有 Gate；一旦它曾捕获 lease，之后即使 CurrentLease 在 rent 时清空，keyed reserve / WU / terminal 仍会进入保留的 Gate，pool rent 本身也要进入一次。[keyed path][keyed]、[pool][pool]。因此“fallback 未启用 B2”不等于该连接所有额外同步都为零；分配为零也不证明 CPU 无回退。需要 pristine legacy 与曾用 B2 后 legacy 两类控制。

信用 waiter 仍是原 LinkedList；`MaxPendingSendStateWaiters=1` 只限制等待新 state 容量的 waiter，**不限制已有 state 上的所有并发申请数**。[contended][contended]。pilot 的生成式 pump 逐 item 等待，正常每流最多一个未授信 helper；不能把此调用约束推广为任意内部多句柄调用的 M≤K。新增实现没有第二个 waiter 队列或新的硬 waiter 上限。

Gi 也不提供 prepared-memory 预算。sized helper 先信用再序列化；unsized 仍受既有 `MaxPreCreditSerializedBytes` 与派生 waiter cap 控制：若预算 B、最大 frame Fm，其子系统长期 serialized payload 上界为 `max(B,Fm)+min(K,max(1,floor(B/Fm)))×Fm`，另加 header/pool capacity rounding。原 SendPump 的 normal/progress 队列、保留字节及 Flush batch 是其他所有权阶段，不能相加成一份虚构的信用窗口，也不能把该 unsized 子系统上界冒充整个 session 内存上界。[预算定义][memory-options]、[预算创建][budget]。

## 6. 真实生成式 RPC 的接入和止损点

仅生成式 **服务端输出**的 `IRpcSizedCodec<T>.CanExactSize` 且当前 item `TryGetEncodedSize` 成功时尝试 capture；每个 pump 只选择一次。首 probe 失败后整流保持 keyed；首 item 无法 exact-size 也不在以后升级。已捕获流后来遇到 unsized item，则 Disable 并回收 Gi，保留 immutable lease 供原 unsized budget / exact admission / refund 使用，不再恢复 grant。[选择与转换][selection]、[unsized helper][unsized]。ServerStreaming 及 Duplex 的服务端输出可命中，ClientStreaming 上行、Duplex 上行与 Unary 是重要控制，不应标成同样优化了两方向。

信用 n 仍是 `max(1, encoded item length)`，不包含协议 header / stream id；sized 实际编码长度必须与预估一致。压缩仍在原 `PrepareOutboundPacket`、排队之前发生，wire 压缩长度不替代该信用单位；序列化、snapshot、pool writer、压缩、Request 前驱与 Flush 策略没有迁移到新的 owner。[sized helper][sized-helper]、[compression][compression]。writer blocked Flush 时本地 Dispose / Gi recall 不依赖 writer 再执行命令，但这只证明回收调用的依赖关系；不能保证网络上的 Cancel / WU 立即出网，更不能替代 blocked-Flush teardown 的真实验收。[原 batch/Flush][pump-flush]。

后续按照以下门槛解释结果，而非因模型 command 数下降直接 Go，或因 D 的独立结果直接否决 B2：

1. **正确性先行。**754 的完整结果与此前 red 分开保留；608 的同 SHA gate 已执行，后续 measurement SHA 仍必须重跑原 25 步。任一取消、异常、信用、buffer 或生命周期失败，先修复并复测，停止性能扩展。[新独立 workflow][screen-workflow]
2. **确认真实启用和边界。**同机 A/B 以 c7 为共同控制，生产树、measurement tree、实际 DLL/native executable 一致；eligible DTO 必须有实际 Activated/LocalItems，byte[] unsized 控制必须保持 fallback。接收完成边界与 WaitForIdle 后 settled CPU/分配分别报告，原样本与失败不可筛除。[测量源码][measurement]
3. **先小范围否证。**保留 TCP/SharedMemory、JIT PGO OFF/ON、实际 NativeAOT、ABBA/BAAB 与 A/A；重点长流高并发、小 item，以及短流、Unary、4 KiB 和原 unsized 路径。只有可重复的完整 RPC 正面证据，才扩大 Q/窗口压力等实验；局部计数与 footprint 不代替这个门槛。
4. **明确定义失败范围。**若新增 state lock/context、压力扫描及原 SendPump 成本吞掉收益，或重现 #803 量级的整体退化，应否决这个 pilot。高并发长流完整 RPC 稳定约 ≥5% 改善是优先目标，还须综合 CPU、分配、尾延迟、NativeAOT 与公平；允许的短流回退不能覆盖 correctness 或整体伸缩性失败。此结论不等于所有未来 B2 / B3 / 自适应设计已被证明不可能。
5. **尚缺完整矩阵。**完整 1/16/64/100/1,000/长流长度、16 B/64 B/4 KiB、c1/8/32/128、压力/耗尽/拥塞/取消、named/mixed stream、公平分布、长期 churn、压缩、blocked Flush、Q 敏感性仍须逐项核验；本次有限 screen 及分配附录即使全部成功，也不足以宣布最终生产验收。

以上成本假设与[跨框架 A/B/C 评审](frameworks-and-candidates.zh-CN.md#15-候选比较与需要实测的静态成本)、[独立正确性发现](correctness-findings.zh-CN.md)衔接；本文件只固定这个真实 pilot 的可审查边界。

## 7. 已执行的分配附录：608 / run 38172711056

[footprint job 114569881578][footprint-job] 在同 SHA 正确性 gate 后运行 A/B，未修改生产源码或测量阈值。[原始 artifact 11715350476][footprint-artifact] 为 7,859,310 B ZIP，SHA256 `d3df3ce6df4fdf3caf968457f239813a790f455bda04b3495f76ce4f22deb9e3`。独立 verifier 不 import 采集器，已核验 127 个 manifest 文件、993 个固定 Git 生产源码记录、10 个 build-policy、7 个 measurement 输入、38 个 binary/dependency 项及原 job 日志中的 31 个 exit=0。三种运行时各 ABBA，合计 12 个实际进程、1,920 条整数分配行、96 组对照，独立重算与原汇总逐项相同。

实际环境为 Ubuntu 24.04.5 LTS / x64，SDK 10.0.112、runtime `.NET 10.0.12`。PGO OFF/ON 的实际进程 flags 已核验；NativeAOT 有 `Generating native code` 原日志、保留 ELF、直接执行命令与动态代码 supported/compiled 均为 false 的输出。[程序身份检查][footprint-identity]。这不是 AOT smoke，也不是 NativeAOT RPC。

下表**每个值在三种运行时及每臂每阶段的 10 个原始样本中均相同**。单位为该阶段每次操作的当前线程托管分配字节；counter 两侧的数组、delegate 创建、诊断、GC 与样本 I/O 排除，body 内的生产操作保留。[计数边界][footprint-counter]。

| 实际阶段 | A | B | B−A | 解释边界 |
|---|---:|---:|---:|---|
| new Lock / 首 Enter-Exit | 40 / 0 | 40 / 0 | 0 / 0 | 相同 primitive 控制，不测锁 CPU |
| 默认 controller 构造 | 400 | 416 | +16 | 整次构造分配，不是 controller 自身 retained size |
| pristine keyed 首 state + exact item | 216 | 240 | +24 | 未启用 lease 的新 state 也受字段扩大影响 |
| 已有 state、首 lazy Lock + lease | 0 | 152 | +152 | 已有 state 的分配阶段 |
| 全新 state + 首 lease | 216 | 392 | +176 | 包含原字典初始化，不是 context 大小 |
| 已有 state / Lock、新 generation lease | 0 | 112 | +112 | 每代 context 分配仍存在 |
| 无 waiter refill / local / WU / refund | 0 | 0 | 0 | 同步完成路径；不是 waiter/TCS 或真实 RPC 分配 |
| Dispose 后合法 late refund / 再回 keyed | 0 | 0 | 0 | 不证明跨代引用回收或 keyed CPU 没有回退 |
| connection Complete 清空已退役池 | 48 | 72 | +24 | 终态成本确实存在；具体对象归因尚无 allocation-stack 采样 |
| 同时创建 128 state：每 controller | 20,080 | 42,608 | +22,528 | 明确 `/128` 后为 +176 B/stream |
| 复用同一批 128 state / Lock：每 controller | 0 | 14,336 | +14,336 | 明确 `/128` 后为 +112 B/new generation |

额外 Disable / empty Dispose / unused-grant Dispose 在 B 中均为 0 B；A 对这三项明确 no-op，不能用其比较 CPU。128-state 阶段先保持全部同时 live，再逐一证明同一批物理 state 按 LIFO 复用，未用一个 hot slot 代替整池。[阶段、适配及物理 identity 断言][footprint-phases]。每个 B 轮实际 Activated=Disposed=46,384、fallback=0、local=10,000、refill=20,000、exact slow=10,000、recall=81,440,000 B、refund=1,222,144 B；A 的八项均为 0。生产计数证明实际使用 grant，非静默 fallback。

112 B 的 context 归因由已有 Lock 时的调用差值与 capture 的唯一新增 context 交叉支持；+24 state、40 Lock、112 context 与首次 +176 相符。这仍是**调用分配与源码归因**，没有测对象存活、retained heap/RSS/native memory，也没有 CPU、延迟、RPC/s 或其他线程分配。ItemBytes=16 仅是 controller 信用单位，本程序没有 payload 或 wire DATA；短预热也不保证 PGO tier 稳态。不能把不同阶段相加成已测 B/RPC，或以无分配 local 路径替代完整 RPC 证据。后续 E2E 必须检验每代 112 B 及额外 state 锁能否被 authority 摊销收益抵消；608 的整体红灯仍保留。

**同源重复证据：**5cab 的 [run 38173700315][footprint-repeat-run] 再次先通过同 SHA gate，再完成 [footprint artifact 11714758862][footprint-repeat-artifact]；原 ZIP SHA256 `1e629fbc9be85591a93f6738d3b4176d0400e4cc16844bd783b4244a054a28bd`。新一轮 31/31 步、独立 JIT/AOT 构建和实际 12 进程均复核通过，完整 1,920 个分配值与 608 按运行时／阶段／臂／轮逐一相同。两轮固定生产及 footprint measurement 内容相同，实际 recipe/编译身份和二进制各自保留；没有复用旧样本或重跑 608 到绿。取得该分配证据时 RPC 尚在执行；最终真实 RPC 另行复核并作出阶段 No-Go，本重复结果的适用范围仍仅为分配附录。

[controller]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs
[grants]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs
[bridge]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs
[unsized]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs#L46-L188
[capture]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L12-L113
[local]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L252-L283
[recall]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L68-L148
[state-fields]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L184-L233
[lease-identity]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L232-L250
[lease-close]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L285-L335
[pool]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.SendGrants.cs#L377-L459
[keyed]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L78-L128
[limits]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L9-L72
[contended]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L131-L204
[returns]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L206-L278
[terminal]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L283-L376
[connection-close]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L494-L532
[oversized]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L628-L641
[admit]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L643-L719
[waiter]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/StreamFlowController.cs#L846-L874
[bridge-pump]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L30-L205
[selection]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L239-L307
[sized-helper]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L309-L405
[server-owner]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Server/ServerGeneratedBridge.cs#L67-L111
[send-packet]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.cs#L181-L213
[pump-enqueue]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.SendPump.cs#L102-L134
[pump-flush]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.SendPump.cs#L285-L341
[compression]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.Compression.cs#L8-L110
[memory-options]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/SharpLinkRuntimeOptions.cs#L59-L83
[budget]: https://github.com/SunSi12138/SharpLink/blob/ba9dfedc3a769a14f03cb5c522f4921f5da655eb/src/SharpLink.Runtime/RpcSession.PreCreditStreaming.cs#L196-L234
[correctness-run]: https://github.com/SunSi12138/SharpLink/actions/runs/38172137769
[correctness-artifact]: https://github.com/SunSi12138/SharpLink/actions/runs/38172137769/artifacts/11714691349
[deadline-red]: https://github.com/SunSi12138/SharpLink/actions/runs/38171089212
[screen-workflow]: https://github.com/SunSi12138/SharpLink/blob/608288167436ce361920e7ee41fa9bb958229707/.github/workflows/flow-b2-rpc-screen.yml
[measurement]: https://github.com/SunSi12138/SharpLink/tree/608288167436ce361920e7ee41fa9bb958229707/test/SharpLink.FlowArchitectureEvidence
[footprint-run]: https://github.com/SunSi12138/SharpLink/actions/runs/38172711056
[footprint-job]: https://github.com/SunSi12138/SharpLink/actions/runs/38172711056/job/114569881578
[footprint-artifact]: https://github.com/SunSi12138/SharpLink/actions/runs/38172711056/artifacts/11715350476
[footprint-identity]: https://github.com/SunSi12138/SharpLink/blob/608288167436ce361920e7ee41fa9bb958229707/test/SharpLink.FlowGrantFootprintEvidence/Program.cs#L23-L130
[footprint-counter]: https://github.com/SunSi12138/SharpLink/blob/608288167436ce361920e7ee41fa9bb958229707/test/SharpLink.FlowGrantFootprintEvidence/Program.cs#L137-L149
[footprint-phases]: https://github.com/SunSi12138/SharpLink/blob/608288167436ce361920e7ee41fa9bb958229707/test/SharpLink.FlowGrantFootprintEvidence/Program.Phases.cs#L68-L320
[footprint-repeat-run]: https://github.com/SunSi12138/SharpLink/actions/runs/38173700315
[footprint-repeat-artifact]: https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11714758862
