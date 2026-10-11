# Issue #735：独立正确性发现、修复与证据边界

本文记录本轮生产架构审计发现的三类**既有 dev 正确性缺陷**，以及相应独立 Draft PR。它们分别涉及 send-state generation 捕获、sized-codec 快照／writer 的异常所有权，以及错误终结后的未发送额度归还。三者都能用固定原始 dev 复现，不以 B2 grant、B3 Ready Writer 或 D 的方向锁改动作为触发前提。因此，其修复必须进入性能比较双方共同的正确性控制，不能计入任何调度器的性能收益。[#849](https://github.com/SunSi12138/SharpLink/pull/849)、[#850](https://github.com/SunSi12138/SharpLink/pull/850)、[#851](https://github.com/SunSi12138/SharpLink/pull/851)

**截至 2026-10-11 20:21:09 UTC 的结论：**三项独立修复均已有实际产品提交及 PR 合成提交的指定 red/green、四轮完整套件通过；共同正确性控制也已实际通过其指定三项 red/green 和四轮 2085 个 case。#850 的固定 40 进程 A/B 控制已经逐份核验，全数通过；但其更早完整测试中的 **24,624 B / 0 B 分配断言失败仍未归因**。原编译失败、完整测试红灯及未执行位置都保留。三项 PR 均为 Draft、均未合并；这些正确性结果不构成 B2 或 B3 的性能 Go。初始 B2 桥接测试另已实际触发相同的错误终结退款缺陷，修复后的集成结果仍须独立验证。[#849 最新回归](https://github.com/SunSi12138/SharpLink/actions/runs/38168008260)、[#850 原完整测试红灯](https://github.com/SunSi12138/SharpLink/actions/runs/38170017685)、[#850 固定控制](https://github.com/SunSi12138/SharpLink/actions/runs/38170670903)、[共同正确性控制](https://github.com/SunSi12138/SharpLink/actions/runs/38171029907)、[B2 桥接 red](https://github.com/SunSi12138/SharpLink/actions/runs/38171089212)

## 1. 固定对象及证据类别

初始性能基线仍是任务开始时确认的 dev `f8ba89121c87a6410a0139c44d7db30eab877f41`，已包含 #840、#841。后续新增的共同正确性控制是另一个明确标识的对象，不能把它冒充为初始 dev，也不能把修复与调度器优化相加。[固定 dev](https://github.com/SunSi12138/SharpLink/commit/f8ba89121c87a6410a0139c44d7db30eab877f41)

| 对象 | 固定提交 | 本文的用途 |
|---|---|---|
| 原始 dev | `f8ba89121c87a6410a0139c44d7db30eab877f41` | 所有独立旧实现 red 的输入；保留 #840/#841。 |
| #849 当前修复 | `018728ae87faeda42f07503f1c9bfc70ee564e0c` | 在原 `_gate` 内捕获 `expectedLease`。 |
| #849 PR 合成提交 | `a4d23dff66b0320f789627870854c522a73159a9` | PR 事件实际 checkout 的对象；与分支 HEAD 分开记录。 |
| #850 首次失败修复 | `ecdf26b1b0e0ec9f229e64a303964550d67eea8f` | 保留错误作用域导致的编译红灯。 |
| #850 修正后的产品 | `9b6951536d6d1451c694fcf49a1fc73b4994d9d8` | 专门回归 green、原完整 r2 allocation red；固定控制矩阵的 B 臂。 |
| #850 控制工作流提交 | `15c9f30d44142995e957fd0cbb9f7a9eddc92782` | 新增固定控制 collector／workflow；其产品 bridge 与 `9b695153` 相同。 |
| #850 PR 合成提交 | `11d6c72e0691ee24372eb3b15689cde5ca41a54c` | 后续 PR ownership 回归实际编译对象；与固定控制的 B=`9b695153` 分开记录。 |
| #851 独立修复 | `57b59f40f1c554c11bbfad2ed732073e279dbde9` | 区分 error completion 与已全额退款的 abort。 |
| #851 PR 合成提交 | `399543b7a9c1a34dc62bc21db036b71305289b27` | PR 回归实际编译对象。 |
| 共同正确性控制 | `c7ba1768f31ce7219dc438dc6009347de6806ed5` | 合并三项生产修复作为后续实验双方的共同前提；不含 D、B2 或 B3。 |

上述单元测试在 GitHub Actions 上实际编译并执行。它们不是 microkernel 吞吐模型，也不是生成式 RPC 的端到端性能对照，更不是实际 NativeAOT 性能验收。原 PR 的 package smoke、CodeQL 或普通 CI green 各自保留其检查范围，不能扩展为未运行的性能、取消或生命周期结论。完整 suite 的计数包含各实验明确安装的测试：`2082 + 8 = 2090`、`2082 + 2 = 2084`、`2082 + 1 = 2083`；这些不是可以混用的同一个测试群体。[#849 collector](https://github.com/SunSi12138/SharpLink/blob/018728ae87faeda42f07503f1c9bfc70ee564e0c/eng/send-credit-lease-race/run.py)、[#850 collector](https://github.com/SunSi12138/SharpLink/blob/9b6951536d6d1451c694fcf49a1fc73b4994d9d8/eng/generated-stream-snapshot-regression/run.py)、[#851 collector](https://github.com/SunSi12138/SharpLink/blob/57b59f40f1c554c11bbfad2ed732073e279dbde9/eng/send-credit-terminal-regression/run.py)

## 2. #849：generation 在退出保护锁后才读取，ABA 校验失去意义

### 2.1 失败发生在什么位置

原 `AcquireSendCreditAsync` 在 `_gate` 内取得 `SendState` 引用，但是退出锁后才计算 `state?.Lease ?? 0L`，然后调用 `AcquireContendedSendCreditAsync`。这使“预期 state”和“预期 generation”不再是同一次受保护观察的结果。在两个临界区之间，另一个线程可以完成旧流、归还额度、将对象回池，再用相同 key 租用同一对象。旧调用读取的是新 lease；第二次进入 gate 时，对象引用与刚读取的 lease 都匹配新流，因而错误通过检查。[原入口](https://github.com/SunSi12138/SharpLink/blob/f8ba89121c87a6410a0139c44d7db30eab877f41/src/SharpLink.Runtime/StreamFlowController.cs#L117-L190)

确定性交错不需要更改池或反射调用私有 contended 方法。测试从真实 keyed 入口进入，在原 probe 退出 gate、contended 参数求值之前暂停；控制线程完成旧 generation、回池、重新注册，再恢复原线程。断言先证明 `sameObject=True`、lease 从 1 变为 2，随后检查是否错误准入，以及新 generation 的额度是否受到影响。[八 case fixture](https://github.com/SunSi12138/SharpLink/blob/018728ae87faeda42f07503f1c9bfc70ee564e0c/eng/send-credit-lease-race/StreamFlowControllerLeaseRaceTests.cs#L17-L59)

fixture 的流／连接窗口是 16/32 B，新流预先占用 8 B，因此新流 credit 与连接 credit 应保持 **8/24 B**。原实现实际接受旧调用，把两者减为 **0/16 B**；该错误消耗属于新 generation。原实现还通过了同一 fixture 中另外七个控制，因此有效 red 要求精确的这一项失败，而不是“任何异常都算复现”。这组小窗口用于确定性制造争用，不改变产品默认窗口。[原始归因 run](https://github.com/SunSi12138/SharpLink/actions/runs/38166410894)、[最新固定旧版 red artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38168008260/artifacts/11712669220)

### 2.2 修复及保证范围

修复在取得 state 的**同一个原有 gate 临界区**内将 generation 存入 `expectedLease`，再把这个局部值传给原 contended 方法。其正确性来自一个简单条件：`(expectedState, expectedLease)` 必须来自同一次受保护观察。旧引用即使指向复用后的同一对象，保存的 lease 仍是 1，因此不能通过新 lease 2 的检查。原有校验、FIFO、pool 上限和窗口记账保持原样。[实际产品修复](https://github.com/SunSi12138/SharpLink/blob/018728ae87faeda42f07503f1c9bfc70ee564e0c/src/SharpLink.Runtime/StreamFlowController.cs#L117-L183)

另外七个控制覆盖不同 key 复用、同 generation 正常归还、复用间隙用户取消、连接终止的原异常实例、完成但未复用、stream abort 的原异常以及原本没有 state 的 tombstone 容量等待。它们检查拒绝 stale 调用的同时不会误拒绝正常进展；它们不证明所有未来接口的归还与生命周期竞态已经覆盖。[fixture 全文](https://github.com/SunSi12138/SharpLink/blob/018728ae87faeda42f07503f1c9bfc70ee564e0c/eng/send-credit-lease-race/StreamFlowControllerLeaseRaceTests.cs)

### 2.3 实际结果与工具可信边界

| 实验 | 实际 subject | 结果与边界 |
|---|---|---|
| 初始归因 [38166410894](https://github.com/SunSi12138/SharpLink/actions/runs/38166410894) | 固定 f8；诊断 overlay 提供修复臂 | 旧版精确 1 failed / 7 succeeded，exit 2；修复臂 4 × focused 8/8、4 × full 2090/2090。用于确认因果。 |
| 分支首次回归 [38167764315](https://github.com/SunSi12138/SharpLink/actions/runs/38167764315) | `92c9ef9b521f67e24d048a320ffbf9ca233a9df1` | 实际 committed product；同一个指定旧版 red；current 四轮 focused、四轮 full 全过。 |
| 首次 PR 回归 [38167792412](https://github.com/SunSi12138/SharpLink/actions/runs/38167792412) | 合成提交 `8f3234ccc29edd30bee08719fb586c25aa383522` | 同样通过；不能用 workflow 的 head metadata 覆盖 artifact 中记录的实际合成 subject。 |
| 最新分支回归 [38168008260](https://github.com/SunSi12138/SharpLink/actions/runs/38168008260) | `018728ae87faeda42f07503f1c9bfc70ee564e0c` | 指定 old red；current 4 × focused 8/8、4 × full 2090/2090，均 exit 0、0 skipped。 |
| 最新 PR 回归 [38168011692](https://github.com/SunSi12138/SharpLink/actions/runs/38168011692) | `a4d23dff66b0320f789627870854c522a73159a9` | 指定 old red；current 4 × focused 8/8、4 × full 2090/2090，均 exit 0、0 skipped。 |

初始归因工具曾含算法 overlay，不能拿它代替真实产品修复。持久回归只对 old/current 注入同一个暂停 hook、只读 snapshot 和 fixture；移除诊断段后必须逐字节恢复输入源码。它不再提供“fixed”算法转换，current 必须直接测试 checkout 的已提交生产代码。这避免产品回退后仍被测试工具偷偷修好。[诊断注入实现](https://github.com/SunSi12138/SharpLink/blob/018728ae87faeda42f07503f1c9bfc70ee564e0c/eng/send-credit-lease-race/apply.py)

原 PR Fast [38167792304](https://github.com/SunSi12138/SharpLink/actions/runs/38167792304) 的文件行数 gate 红灯保留；后续 `018728ae` 仅压缩参数排版以满足原门槛，未放宽 gate。最新原 CI 的 [PR Fast](https://github.com/SunSi12138/SharpLink/actions/runs/38168011674)、[Package Smoke](https://github.com/SunSi12138/SharpLink/actions/runs/38168011698)、[CodeQL](https://github.com/SunSi12138/SharpLink/actions/runs/38168011826) 均为 success。该结论是此提交的检查结果，不是自动合并授权。

## 3. #850：快照释放抛错导致重复 release、writer 泄漏或原异常被覆盖

### 3.1 两条独立的所有权失败

原 `SendStreamChunkKnownSizeAsync` 在 `SerializeSized` 后调用 `ReleaseSnapshot`，**调用成功后**才把本地 snapshot 置空。最外层 finally 又在 snapshot 非空时调用 release，然后归还 writer。用户 codec 的 release 一旦抛异常，就能同时破坏一次性所有权和后续 cleanup 控制流。[原 helper](https://github.com/SunSi12138/SharpLink/blob/f8ba89121c87a6410a0139c44d7db30eab877f41/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L275-L329)

| 确定性输入 | 原实现实际观测 | 应保持的不变量 |
|---|---|---|
| Serialize 成功，第一次 snapshot release 抛错 | `primaryPreserved=True, releaseCalls=2, writerReturned=False` | snapshot 的 release 调用只能由一个 owner 执行一次；未发布 writer 必须得到归还机会。 |
| Serialize 先抛原异常，finally 的 snapshot release 再抛错 | `primaryPreserved=False, releaseCalls=1, writerReturned=False` | secondary cleanup 不能替换已经选定的序列化原异常；writer cleanup 不能被前一段 release 跳过。 |

上述结果来自同一个可移植 fixture 对原始 f8 的实际执行，原错误不是靠 mock 计数推断：fixture 捕获真实 `IRpcByteBufferWriter`，通过其 `WrittenCount` 是否已抛 `ObjectDisposedException` 观察实际归还状态。测试每次使用独立 runtime context，先检查结果，再在测试自身 finally 处理旧实现泄漏的 writer；测试清理不会被报告成被测路径成功。[永久 fixture](https://github.com/SunSi12138/SharpLink/blob/9b6951536d6d1451c694fcf49a1fc73b4994d9d8/test/SharpLink.UnitTests/Runtime/GeneratedStreamSnapshotOwnershipTests.cs)、[old red 原始产物](https://github.com/SunSi12138/SharpLink/actions/runs/38169839441/artifacts/11714101372)

### 3.2 修复的异常优先级

实际修复先把 snapshot 转移到局部 `snapshotToRelease`，并清空 helper 的 ownership 字段，再调用 codec release。若主操作已经失败，catch 保存其原异常；异常清理中的 secondary snapshot-release failure 不再覆盖该原异常。writer Return 位于嵌套 finally，因而 snapshot cleanup 抛错不会跳过 writer cleanup。[修复后的 helper](https://github.com/SunSi12138/SharpLink/blob/9b6951536d6d1451c694fcf49a1fc73b4994d9d8/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L275-L352)

这里必须区分“唯一调用”“尝试清理”与“资源确已释放”：本修复保证 snapshot release 只有一次 owner 调用，并保证执行到 writer-return 清理；测试使用的真实 pool 确认 writer 已归还。但如果第三方 codec 的 release 自身失败，其内部资源是否已经释放无法由 Runtime 猜测，不能声称 cleanup 已成功。`Buffers.Return` 本身也抛异常时的优先级不在这两个 fixture 的覆盖范围；本文不把有限的 serializer-versus-snapshot 异常契约扩写成“任意 cleanup 异常均保持原异常”。[修复源码](https://github.com/SunSi12138/SharpLink/blob/9b6951536d6d1451c694fcf49a1fc73b4994d9d8/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L322-L351)

### 3.3 编译红灯和完整测试红灯必须单独保留

| Run | 实际输入与结果 | 验收结论 |
|---|---|---|
| [38169839441](https://github.com/SunSi12138/SharpLink/actions/runs/38169839441) | old 成功编译并精确 2/2 指定失败；current `ecdf26b1` 编译 exit 1，`operationError` 被误放到另一个方法，出现 CS0219 与 CS0103。 | current 的任何测试均未执行。该工程错误不算“修复通过”。 |
| [38170017685](https://github.com/SunSi12138/SharpLink/actions/runs/38170017685) | old 再次精确 2/2 指定失败；current `9b695153` 编译成功，4 × focused 2/2 green；full r0/r1 各 2084/2084；r2 为 2083 succeeded / 1 failed，exit 2。 | 专门 ownership 回归通过；完整套件未通过。 |
| 同一 run 的 full r3 | collector 在 r2 非预期失败后停止。 | **NotRun**，不能算成功或计入四轮完整测试。 |
| 后续 push [38170670782](https://github.com/SunSi12138/SharpLink/actions/runs/38170670782) | 实际 subject `15c9f30d`；old 指定 2 red、exit 2；current 4 × focused 2/2、4 × full 2084/2084，均 exit 0、0 skipped。 | 本次八个 current 进程通过；未重写先前的 allocation red。 |
| 后续 PR [38170674741](https://github.com/SunSi12138/SharpLink/actions/runs/38170674741) | 实际合成 subject `11d6c72e`；old 指定 2 red、exit 2；current 4 × focused 2/2、4 × full 2084/2084，均 exit 0、0 skipped。 | 实际 PR checkout 的相同回归通过，不冒充固定 A/B 控制的 `9b695153` 二进制。 |

`ecdf26b1` 的编译失败已经通过正确作用域修正；没有通过删断言或放宽编译告警修正。`9b695153` 的 allocation 失败则尚未归因，两者不能合并成一个“已修复的问题”。[编译红 artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38169839441/artifacts/11714101372)、[完整测试红 artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38170017685/artifacts/11713268189)

## 4. #850 allocation 红灯：已知事实、控制计划与不能下的结论

### 4.1 原测量真正记录了什么

失败方法是 `ReadOwnershipPipeReaderLifetimeTests.SuccessfulSuspensionsShouldNotAllocateAfterWarmup`。它在同一个同步测试方法中进行 1,000 次预热和 10,000 次测量，每轮依次执行 `ReadAsync`、fake reader 同步 `Publish`、`GetResult`、`AdvanceTo`，以 `GC.GetAllocatedBytesForCurrentThread()` 的差值断言精确零分配。原 r2 记录 **24,624 B**，不是从整个进程 GC 总量换算的 B/item，也不是 RPC 性能数据。[测试正文](https://github.com/SunSi12138/SharpLink/blob/9b6951536d6d1451c694fcf49a1fc73b4994d9d8/test/SharpLink.UnitTests/Runtime/ReadOwnershipPipeReaderLifetimeTests.cs#L421-L442)

两固定提交的 `ReadOwnershipPipeReaderLifetimeTests.cs` 与包含 `ReadOwnershipPipeReader` 的 `TransportConnection.cs` 逐字节相同；前者 SHA256 为 `6a562ab31bccfe8dd137f8a7f66c82830e752807507f769fb78e2d89c39895c4`，后者为 `22f2232b77f8746d7525adfbbd6493f9554ef0b2d671df4572276a5f1210b2e3`。快照修复不在该测试直接调用链上。这能排除“直接修改了被测 reader 源码”，但不能单独排除全套调度、运行时或间接干扰。[reader 实现](https://github.com/SunSi12138/SharpLink/blob/9b6951536d6d1451c694fcf49a1fc73b4994d9d8/src/SharpLink.Runtime/Transport/TransportConnection.cs#L104-L245)、[两产品的固定差异](https://github.com/SunSi12138/SharpLink/compare/f8ba89121c87a6410a0139c44d7db30eab877f41...9b6951536d6d1451c694fcf49a1fc73b4994d9d8)

原 r0/r1 的该方法分别运行约 48.9013/18.3447 ms 并通过；r2 约 19.7641 ms 失败。原 TUnit JSON 显示三个时间窗都有其他 ReadOwnership dispatch 测试或并发压力测试重叠，但没有分配调用栈。只有时间重叠不能证明某个测试导致了 24,624 B；运行时间长短也没有给出单调因果。[原 r0/r1/r2 HTML 与 JSON 所在 artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38170017685/artifacts/11713268189)

### 4.2 运行时源码否定了哪些草率解释

`ReadOwnershipPipeReader.AwaitReadAsync` 使用 `PoolingAsyncValueTaskMethodBuilder<>`。本次实际查阅了 .NET **10.0.12** 的 `PoolingAsyncValueTaskMethodBuilderT.cs`，Git blob 为 `150f22c1970325b57c18b22f8cd7663ba21208b0`：`RentFromCache` 先读取 `[ThreadStatic]` 单 box 缓存，再尝试 per-core slot，最后才分配新 box；`GetResult` 的 finally 清空 state/context 并归还缓存。这段实现不能支持“其他线程直接抢走了本线程 TLS box”或“普通 GC 自动清空这个 TLS pool”的归因。[租用／归还代码](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/PoolingAsyncValueTaskMethodBuilderT.cs#L284-L345)、[GetResult](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/PoolingAsyncValueTaskMethodBuilderT.cs#L418-L440)

这不是零分配的完整运行时证明。ExecutionContext、awaiter、JIT 和运行环境仍可能影响实际分配；现有原始数据没有定位到具体分配类型或调用栈。本文把这些保留为待测假设，不把任何一个写成已证实根因。运行时源码原文及 blob 身份另外保存在 evidence archive 的 `evidence/b2-bridge/pooling-async-value-task-builder-v10.0.12.json`。

### 4.3 固定的补充控制矩阵

[控制 run 38170670903](https://github.com/SunSi12138/SharpLink/actions/runs/38170670903) 使用 [已提交 collector](https://github.com/SunSi12138/SharpLink/blob/15c9f30d44142995e957fd0cbb9f7a9eddc92782/eng/generated-stream-snapshot-regression/control_matrix.py) 和 [独立 workflow](https://github.com/SunSi12138/SharpLink/blob/15c9f30d44142995e957fd0cbb9f7a9eddc92782/.github/workflows/generated-stream-snapshot-controls.yml)。源码固定为 A=f8、B=`9b695153`，不是根据每次结果选择新的 commit。

| 群体 | 事前计划 | 不变条件 |
|---|---|---|
| 完整套件 | 4 对，AB/BA/AB/BA，共 8 个独立进程 | A 每次 2082 个原 case；B 每次 2084，含已有两个 snapshot 回归；不向 A 复制新测试。 |
| allocation 单 case | 16 对，交替 AB/BA，共 32 个独立进程 | 仅过滤到原失败方法；方法源码、预热次数、循环次数、零阈值均不改。 |

两组使用同一 runner 环境、`TUNIT_MAX_PARALLEL_TESTS=8`。Collector 在构建前声明全部 40 个位置，逐字节校验 tracked files，只允许那一个 production bridge 和两个新增回归的既定差异；每次测试前后再次核对 source/binary 身份。每次新建并归档 HTML/JSON，验证唯一 `publicationGeneration`、准确 case 数和实际目标方法。每一处 failure、timeout、missing report、source/binary drift 均令最终 workflow 红灯；测试失败不会删除后续已声明对照或触发自动 retry。[固定 collector](https://github.com/SunSi12138/SharpLink/blob/15c9f30d44142995e957fd0cbb9f7a9eddc92782/eng/generated-stream-snapshot-regression/control_matrix.py)

注意 full 的 A/B 本来就分别有 2082/2084 个 case，新增两例可能改变全套的调度环境；因此“只在 B 的 full 中再现”仍不能仅凭相关性把原因归给 bridge 的生产算法。单 case 群体用于进一步分离这个边界。即使 40 个新进程全绿，也只能报告本次控制未再现，不能把原来的 24,624 B 抹去或宣布已证明是无关 flake。

### 4.4 固定控制的实际执行结果

新 artifact 已完整下载并验证 ZIP 的 GitHub SHA256，独立解码每份 HTML 内嵌的 gzip JSON，与 TUnit 原始 JSON 逐 case 对照，再与 stdout 和进程退出码交叉核对。结果如下；这张表只包含 [38170670903](https://github.com/SunSi12138/SharpLink/actions/runs/38170670903) 的事前声明 40 个位置，不把后续 ownership 回归、原失败 run 或不同提交混入同一个群体。[原始 artifact 11713659187](https://github.com/SunSi12138/SharpLink/actions/runs/38170670903/artifacts/11713659187)

| 固定对象／群体 | 计划／实际进程 | 每进程原始结果 | allocation 目标出现次数 |
|---|---|---|---|
| A=f8，完整套件 | 4 / 4 | 每次 2082 passed，0 failed/skipped，exit 0 | 4，均通过原零分配断言。 |
| B=`9b695153`，完整套件 | 4 / 4 | 每次 2084 passed，0 failed/skipped，exit 0 | 4，均通过原零分配断言。 |
| A=f8，单 case | 16 / 16 | 每次 1 passed，0 failed/skipped，exit 0 | 16，均通过原零分配断言。 |
| B=`9b695153`，单 case | 16 / 16 | 每次 1 passed，0 failed/skipped，exit 0 | 16，均通过原零分配断言。 |

40 个实际位置严格对应原 AB/BA 计划，没有 NotRun、pair 缺失、重复位置或自动 retry；40 个 `publicationGeneration` 各自唯一。187 项内部文件的大小与 SHA256、ZIP entry 和解压文件字节均一致。另把 artifact 的 A=1581／B=1585 项 tracked-source 身份逐项与固定 Git blob 的实际字节重新比较，全部吻合；原目标测试和 reader 源码保持相同。每臂 155 项编译文件身份清单与各进程前后的 unchanged 记录一致。该 artifact 保存的是编译文件的 hash 清单，不能据此声称在本地重新执行或重新读取了未打包的全部 DLL。[collector 的身份核验](https://github.com/SunSi12138/SharpLink/blob/15c9f30d44142995e957fd0cbb9f7a9eddc92782/eng/generated-stream-snapshot-regression/control_matrix.py)、[完整 artifact](https://github.com/SunSi12138/SharpLink/actions/runs/38170670903/artifacts/11713659187)

实际 Runtime DLL 身份分别为 A=`5304f5aef58c8dc93affff03c034103e4e62a3cbe2bf8f554a3ae870b0bed69d`、B=`6b71b6b18b0b6cbf70c3f617635e634ac084c0b8201dffa959024d8517229fff`；TUnit JSON 的 `commitSha` 继承 workflow 的 `15c9f30d`，不能覆盖这两臂固定源码、生产 tree 和二进制清单的 provenance。执行运行时为 .NET 10.0.12、TUnit 1.72.16.0。

本次结论是：**固定控制没有再次触发 allocation 断言失败。**通过的报告没有提供分配栈或每次分配类型；本文不编造额外的“测量得到 0 B”数值样本，也不把这 40 次通过当成原 24,624 B 根因已经解决。原 red 与新控制的全部原始记录、独立解码脚本 `evidence/new/snapshot-correctness-independent-audit.py` 和逐进程审计结果 `evidence/new/38170670903/independent-snapshot-audit.json` 一起保留。

## 5. #851：error completion 不等于额度已经全部归还

### 5.1 信用守恒与终结异常是两个状态维度

原 `CompleteSendStream(error)` 把 stream 标记为完成并记录原异常，但保留已经扣除的实际额度，等待 in-flight 数据的 WindowUpdate 或未发布 helper 的 ReturnUnsent。`AbortSendStreams` 则立即归还该请求下各流的全部 outstanding debt。两条路径都会写入 `AbortException`；原 `ReturnUnsentCredit` 却把 `AbortException != null` 一概当成“已经退款”，忽略仍应归还的未发送额度。[原 ReturnUnsent 与 Complete](https://github.com/SunSi12138/SharpLink/blob/f8ba89121c87a6410a0139c44d7db30eab877f41/src/SharpLink.Runtime/StreamFlowController.cs#L263-L341)、[原 Abort](https://github.com/SunSi12138/SharpLink/blob/f8ba89121c87a6410a0139c44d7db30eab877f41/src/SharpLink.Runtime/StreamFlowController.cs#L343-L364)

在没有 grant 的基线中，以 $D_i$ 表示实际已经 admitted、尚未结清的字节，则：

$$
W_s=C_i+D_i,\qquad W_c=F+\sum_i D_i.
$$

`Complete(error)` 只改变终结状态，不改变这两个等式中的额度。若一个 helper 尚拥有从未发布的 $n$ 字节，后来 `ReturnUnsent(n)` 必须令 $C_i$ 与 $F$ 各增加 $n$、令 $D_i$ 减少 $n$。相反，full abort 已把该 $D_i$ 全部归还；同一个迟到 helper 不能再归还一次。原代码错误地用异常的有无代替了这项结算事实。[实际修复差异](https://github.com/SunSi12138/SharpLink/commit/57b59f40f1c554c11bbfad2ed732073e279dbde9)

这条交错在生成式服务端桥接中有实际调用依据：protocol pump 可以因 deadline 先结束，而已经 admitted 的非合作 serializer 仍在执行；服务端 owner 随后按已选定 terminal 调用 `CompleteSendStream(error)`，或映射异常后发送错误终帧并 Complete。迟到的 helper 清理不能被这个终态错误地当成重复退款。[Server owner](https://github.com/SunSi12138/SharpLink/blob/f8ba89121c87a6410a0139c44d7db30eab877f41/src/SharpLink.Server/ServerGeneratedBridge.cs#L66-L111)、[错误终帧的 finally](https://github.com/SunSi12138/SharpLink/blob/f8ba89121c87a6410a0139c44d7db30eab877f41/src/SharpLink.Runtime/RpcSession.PreCreditTerminal.cs#L6-L47)

### 5.2 最小直接复现和修复

同一个 portable controller fixture 使用 64 B 的流／连接窗口，分别执行以下两臂。它只调用既有 keyed API，不依赖 B2 接口，也没有生产 hook 或算法 overlay。[fixture](https://github.com/SunSi12138/SharpLink/blob/57b59f40f1c554c11bbfad2ed732073e279dbde9/test/SharpLink.UnitTests/Runtime/SendCreditTerminalAccountingTests.cs)

| 步骤 | Complete(error) 臂 | Full-abort 控制臂 |
|---|---|---|
| Acquire 8 B | 可用连接额度 56 B | 可用连接额度 56 B |
| 选定终态 | `CompleteSendStream(error)`；仍欠 8 B | `AbortSendStreams` 全部归还；可用 64 B |
| 相同 late ReturnUnsent(8) | 原版错误忽略：仍为 56 B，retained=1；修复必须恢复 64 B 并退役。 | 必须忽略，保持 64 B；该控制在调用 late refund 时 state 仍存在，避免仅测试 missing-key 短路。 |
| 完成控制臂生命周期 | 应无 retained state | 后续 Complete 后无 retained state。 |

修复在原 `SendState` 上增加 `CreditRefundedByAbort` 布尔标记，仅由 full-abort 的实际退款路径设置，并在 pooled state 重新租用时清除。ReturnUnsent 的短路依据改为这个结算标记；正常超额归还检查仍在，#840 的有效 WindowUpdate delta 公式保持不变。它没有新增字节余额、状态表、队列或公共 API。这里描述的是一个逻辑布尔字段，未把其物理对象布局成本测量为零。[ReturnUnsent](https://github.com/SunSi12138/SharpLink/blob/57b59f40f1c554c11bbfad2ed732073e279dbde9/src/SharpLink.Runtime/StreamFlowController.cs#L258-L285)、[full-abort 标记](https://github.com/SunSi12138/SharpLink/blob/57b59f40f1c554c11bbfad2ed732073e279dbde9/src/SharpLink.Runtime/StreamFlowController.cs#L338-L355)、[commit](https://github.com/SunSi12138/SharpLink/commit/57b59f40f1c554c11bbfad2ed732073e279dbde9)

### 5.3 实际执行与尚未被证明的集成边界

[Run 38170377186](https://github.com/SunSi12138/SharpLink/actions/runs/38170377186) 的固定原 f8 编译成功，精确产生一个指定 red：`completeBefore=56, completeAfter=56, completeRetained=1`；其 full-abort 控制仍为 64 B 且最终正常退役。实际修复提交 `57b59f40` 编译成功，四轮 focused 1/1、四轮完整 suite 2083/2083，全部 exit 0、0 skipped。Collector 只能安装相同 fixture，不能修改生产算法。[原始 artifact 11713587342](https://github.com/SunSi12138/SharpLink/actions/runs/38170377186/artifacts/11713587342)、[collector](https://github.com/SunSi12138/SharpLink/blob/57b59f40f1c554c11bbfad2ed732073e279dbde9/eng/send-credit-terminal-regression/run.py)

后续 PR [run 38170896610](https://github.com/SunSi12138/SharpLink/actions/runs/38170896610) 的实际 subject 为合成提交 `399543b7a9c1a34dc62bc21db036b71305289b27`：同一个 old 指定 1 red、exit 2，current 四轮 focused 1/1 和四轮完整 2083/2083 均通过。其 64 项内部文件、原 ZIP、HTML/JSON 逐 case、stdout、退出码与 binary 身份记录均已独立核验。它仍是直接 controller 的正确性证据。[PR artifact 11714008719](https://github.com/SunSi12138/SharpLink/actions/runs/38170896610/artifacts/11714008719)

独立 B2 桥接 fixture 特意把 owner error 放在解除 serializer 屏障**之前**，避免通过延后 terminal 掩盖竞态。两次执行必须分开记录：

| 候选／run | 实际发生的事 | 证据边界 |
|---|---|---|
| 首个候选 [38170726573](https://github.com/SunSi12138/SharpLink/actions/runs/38170726573) | 新 fixture 的 `int`→`uint` 参数错误导致 CS1503；focused/full 均未执行。 | 编译 red，不能写成运行时 deadline 失败。 |
| 仅修正类型转换后的 `d472eee7cb2097c33342b20df4f394269f864824`，[38171089212](https://github.com/SunSi12138/SharpLink/actions/runs/38171089212) | 编译通过；focused 41 中 40 passed、1 failed、exit 2。唯一失败是 `DeadlineAfterAdmissionMustLetItsAbandonedItemRefundAcrossTheOwnerErrorTerminal`，耗时 142.8388 ms。 | 这是实际桥接级 red；后续四轮 full、其余测试与性能运行均未开始。 |

在第二次运行中，deadline 已被正确选定，owner 已发布 error terminal，随后 serializer 屏障被解除；fixture 又等到 snapshot 释放和真实 writer 归还完成，最后仍观测 **F=0、D=64、G=0**，应为 **64/0/0**。错误发生在 `AssertFullyRetired`，不是等待屏障的 timeout。这确认基线的 error-completion 退款缺陷在新 lease 相关桥接路径上也实际可达。[固定 fixture 的终结／解除屏障／清理顺序](https://github.com/SunSi12138/SharpLink/blob/d472eee7cb2097c33342b20df4f394269f864824/test/SharpLink.UnitTests/Runtime/B2GeneratedStreamBridgeTests.Deadlines.cs#L35-L60)、[原始 artifact 11713264954](https://github.com/SunSi12138/SharpLink/actions/runs/38171089212/artifacts/11713264954)

这组测试使用明确的测试用 sized codec 调用实际桥接和 session helper，属于确定性集成单元测试；它不是生成代理经 TCP／SharedMemory 的完整 RPC 性能证据。#851 controller green 不能冒充修复后的 B2 桥接 green；后续导入相同正确性修复必须继续运行原用例、原断言和完整门槛。在此记录时，修复后的 B2 候选尚未完成独立验证，不能讨论性能 Go。

## 6. 后续性能对照的共同正确性控制

分支 `research/735-common-correctness-control-20261011` 的固定提交是 [`c7ba1768f31ce7219dc438dc6009347de6806ed5`](https://github.com/SunSi12138/SharpLink/commit/c7ba1768f31ce7219dc438dc6009347de6806ed5)。相对 f8 的 production diff 仅涉及两个既有文件，共合入上述三项修复：

| production 文件 | 固定 SHA256 |
|---|---|
| `src/SharpLink.Runtime/StreamFlowController.cs` | `0c1b1f2565697fb6dfa6097e16fdceda28d3ba4aba032ba28b3d8933ab223af4` |
| `src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs` | `531753a3f2fb54f1a596b854d3af30cd4f3625eef3c2bcfc955794eb1dd64075` |

该提交没有 D 的方向锁、B2 grant 或 B3 Ready Writer。其 [共同回归 collector](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/eng/issue735-common-correctness/run.py) 使用相同的两项 snapshot fixture 与一项 terminal-accounting fixture，共三个新增 case；其完整计数应为 `2082 + 3 = 2085`。这三个 focused case 的数量不代表 lease 的八项 hook 回归也被包含；#849 的八项独立证据仍单独保留。[共同 workflow](https://github.com/SunSi12138/SharpLink/blob/c7ba1768f31ce7219dc438dc6009347de6806ed5/.github/workflows/issue735-common-correctness.yml)

[共同 run 38171029907](https://github.com/SunSi12138/SharpLink/actions/runs/38171029907) 已实际完成：old 原 f8 对相同三个 case 精确 3 red、exit 2，分别复现两项 snapshot 所有权失败与一项 terminal 退款失败；current 四轮 focused 3/3、四轮完整 **2085/2085** 全部通过，exit 0、0 skipped。原 ZIP 及内部 64 项文件通过逐字节／hash 核验，九次测试的 HTML/JSON、stdout、退出码和唯一 publication identity 相互吻合，current 生产源码没有由工具补丁替换。实际 current Runtime DLL SHA256 为 `32e5dbd24b06d772807c986a8cd708e694339ca7902251e22f0d8b01da244f6f`。[共同 artifact 11713588029](https://github.com/SunSi12138/SharpLink/actions/runs/38171029907/artifacts/11713588029)

该结果建立了已经验证的共同正确性输入，不意味着已合并、已覆盖全部运行时，或已经解释了 allocation 红灯。后续性能报告必须同时列出初始 f8、共同正确性控制、候选、measurement 源码和实际二进制身份；只有在双方包含相同正确性修复时，差值才可以讨论为候选调度器的变化。不能从 f8→共同控制的收益中挑出一部分算作 B2，再把共同控制→B2 的收益相加。

## 7. 结论分层与当前未决事项

| 分类 | 可以写入结论的内容 | 不能延伸出的结论 |
|---|---|---|
| 已由确定性 red/green 证实 | #849 gate 外读取新 lease 能使旧申请消耗新流额度；gate 内捕获修正该交错。 | 尚未测到的所有 stale-handle／归还／关闭竞态一并正确。 |
| 已由指定旧版 red 与专门 green 证实 | #850 重复 release、未发布 writer 未归还、serializer 原异常被 snapshot cleanup 覆盖；修复的两条专门路径及后续分支／PR 四轮完整套件通过。 | 先前没有任何套件失败；任意 pool Return 抛错也保持原异常；codec 内部失败清理已经成功。 |
| 已由确定性 red/green 证实 | #851 Complete(error) 与 full abort 的结算语义必须区分，直接 controller fixture 已证明修正；初始 B2 的真实桥接用例也观测到同类未退款 red。 | 修复后的生成式 RPC、非合作 serializer 和 B2 lease 组合已经端到端验收。 |
| 已观察、尚未归因 | #850 原完整 r2 在原零分配测试中得到 24,624 B；同 run 的另外两次完整测试通过。 | 把它认定为任意具体 runtime、调度或修复原因，或者删除为“无关 flake”。 |
| 已完成的固定控制 | f8／9b 的 40 个预声明 A/B 进程全部通过；共同正确性输入的四轮 2085 suite 通过。 | 40 次未再现就证明原 allocation 原因不存在；共同控制已经提供 B2 性能收益。 |
| 有源码边界的合理怀疑 | 完整测试的并发环境、运行时路径或间接干扰值得继续调查。 | 仅凭时间重叠就归责某个并行测试；TLS pool 被其他线程或普通 GC 随意清空。 |
| 待完成的独立门槛 | 原 allocation 的具体归因、修复后的 B2 桥接与完整正确性验证、后续真实 RPC 性能比较。 | 用这些正确性补丁作 B2 总体 Go 或 No-Go；用普通单元测试代替 JIT/NativeAOT RPC A/B。 |

## 附录 A：原始产物索引

下列路径相对于交付的 evidence archive，不是要求把大型 ZIP 放入 Git。各 correctness artifact 保存原始 source／patch／binary 身份和日志。最新 #849 两份、#850 原两份及新三份、#851 分支／PR 两份、共同控制一份均已重新计算 ZIP SHA256，并逐项检查其内部 `files.sha256.json`，未发现缺失或 hash mismatch；B2 artifact 使用其独立 `artifact-manifest.json`。旧版的指定失败与非预期失败都保留，不能只分发绿色日志。

| Run / artifact | Archive 路径 | 原 ZIP SHA256 |
|---|---|---|
| [38166410894 / 11712012693](https://github.com/SunSi12138/SharpLink/actions/runs/38166410894/artifacts/11712012693) | `evidence/new/38166410894/lease-race/`；初始因果 overlay | `e5dc1b0ae73bffc11f7f19f98ed0161bea5eaef71ab9486f98f46d3298b7811b` |
| [38167764315 / 11712718756](https://github.com/SunSi12138/SharpLink/actions/runs/38167764315/artifacts/11712718756) | `evidence/new/38167764315/lease-regression/` | `09d6e2e450d222b6b330a5a43939333c6efc295c08c1416d6338c75157e312bb` |
| [38167792412 / 11712514064](https://github.com/SunSi12138/SharpLink/actions/runs/38167792412/artifacts/11712514064) | `evidence/new/38167792412/lease-regression/` | `80c15e0bdca8c7b6aad5b10bab6700d8e0bbd8b4fa0c133a69863cb472370b4c` |
| [38168008260 / 11712669220](https://github.com/SunSi12138/SharpLink/actions/runs/38168008260/artifacts/11712669220) | `evidence/new/38168008260/lease-regression/` | `1ed6b74a55b4b86234ffc68388ac98c9522b3c213e6d5905eea6e080d44e5a3d` |
| [38168011692 / 11712863722](https://github.com/SunSi12138/SharpLink/actions/runs/38168011692/artifacts/11712863722) | `evidence/new/38168011692/lease-regression/` | `09c8e5686170cb706e76045dc8de6021db15b9ec7d038c4c40e01108e202f4b7` |
| [38169839441 / 11714101372](https://github.com/SunSi12138/SharpLink/actions/runs/38169839441/artifacts/11714101372) | `evidence/new/38169839441/snapshot-regression/`；编译红 | `dc1231d23cbc60a286668d8aaf27b24e64f1fa98bc809a1f8d38ebe17a02a626` |
| [38170017685 / 11713268189](https://github.com/SunSi12138/SharpLink/actions/runs/38170017685/artifacts/11713268189) | `evidence/new/38170017685/snapshot-regression/`；allocation 红 | `e69c9f2bbdad8ce8271da457041fa7d78b6a251c7b7782ec16b8c8ccc0458e0f` |
| [38170670782 / 11713043932](https://github.com/SunSi12138/SharpLink/actions/runs/38170670782/artifacts/11713043932) | `evidence/new/38170670782/snapshot-regression/`；后续 push | `bd408ce0fdf13193c03a98f42e43ee3bca39e193c239f50ab6f18f319936819f` |
| [38170674741 / 11713477637](https://github.com/SunSi12138/SharpLink/actions/runs/38170674741/artifacts/11713477637) | `evidence/new/38170674741/snapshot-regression/`；后续 PR | `fb4fe621dd182c390d5941ff9fe5b513a9e3deb4ce5d7c751b07ae767af2158a` |
| [38170670903 / 11713659187](https://github.com/SunSi12138/SharpLink/actions/runs/38170670903/artifacts/11713659187) | `evidence/new/38170670903/snapshot-controls/`；固定 40 进程 | `e6be72ce0b712220db6a6e33e05839de56705dc6e8dd74a9dbeae75df13e52a0` |
| [38170377186 / 11713587342](https://github.com/SunSi12138/SharpLink/actions/runs/38170377186/artifacts/11713587342) | `evidence/new/38170377186/terminal-regression/` | `239c1365cb65ee3a05e969cd00cea0f74a943a82d5d5888add494b4cff5d9f8a` |
| [38170896610 / 11714008719](https://github.com/SunSi12138/SharpLink/actions/runs/38170896610/artifacts/11714008719) | `evidence/new/38170896610/terminal-regression/`；PR | `53b622fbb4b4c7b7405ab885886a35cab211fb867416a19d47f7ce42c1062156` |
| [38171029907 / 11713588029](https://github.com/SunSi12138/SharpLink/actions/runs/38171029907/artifacts/11713588029) | `evidence/new/38171029907/common-correctness/` | `788d3073a6566f2099175871aa85293943e97403fd421604d900b9827ab07153` |
| [38171089212 / 11713264954](https://github.com/SunSi12138/SharpLink/actions/runs/38171089212/artifacts/11713264954) | `evidence/new/38171089212/b2-correctness/`；实际桥接 red | `405a7b2072a7aa412b245ac67a4adf8d604176e1a1a3aa45f0b6b17e5ddf2beb` |

关键可复核文件包括 `provenance.json`、`exits.json`、`test-checks.json`、`old-to-current-product.patch`、`old/current-overlay.json`、`old/current-binaries.sha256.json` 和每次独立命名的 stdout/stderr、HTML、TUnit JSON。#849 初始归因 artifact 的命名不同，仍以其 `original/fixed` 日志和 overlay 为准，不重命名成持久工作流的执行证据。

新五份 artifact 的独立审计保存在各 run 目录下的 `independent-snapshot-audit.json` 或 `independent-common-terminal-audit.json`；对应只读脚本为 `evidence/new/snapshot-correctness-independent-audit.py`、`evidence/new/common-terminal-independent-audit.py`。它们只读取保存的执行结果，解码 HTML/JSON、重新计算 hash 和计数，不执行产品、不改变原 collector，也不产生可追加到性能 A/B 的样本。

## 附录 B：后续状态记录

此附录追加新运行的结果，不能回写删除前文的原 red。截止 2026-10-11 20:09:38 UTC，只读 GitHub 查询记录：

- #849：HEAD `018728ae`；最新分支／PR 独立回归和原 PR Fast、Package Smoke、CodeQL 全部 success，详见第 2.3 节。
- #850：HEAD `15c9f30d`；[allocation controls 38170670903](https://github.com/SunSi12138/SharpLink/actions/runs/38170670903) 仍 in_progress；后续分支／PR ownership workflow 状态为 success，原 [PR Fast 38170674766](https://github.com/SunSi12138/SharpLink/actions/runs/38170674766)、[CodeQL 38170674727](https://github.com/SunSi12138/SharpLink/actions/runs/38170674727) 为 success，Package Smoke 尚在运行。原 allocation 根因仍未确定。
- #851：HEAD `57b59f40`；已复核的独立 run success；[PR Fast 38170896575](https://github.com/SunSi12138/SharpLink/actions/runs/38170896575)、[PR Package Smoke 38170896580](https://github.com/SunSi12138/SharpLink/actions/runs/38170896580)、[CodeQL 38170896598](https://github.com/SunSi12138/SharpLink/actions/runs/38170896598)、[PR 独立回归 38170896610](https://github.com/SunSi12138/SharpLink/actions/runs/38170896610) 尚在运行。
- 共同正确性控制 `c7ba1768`：已提交输入与明确测试计划，不在尚未取得原始执行结果时声称其四轮 full 已通过。

**追加：2026-10-11 20:21:09 UTC。**下列结果已补齐原始 artifact 核验，前面的 20:09:38 记录作为当时状态保留：

- #850：固定 40 进程控制原始结果全部通过；后续 push／PR ownership 回归各指定 old 2 red、current 四轮 focused 与四轮 2084 全过，见第 3.3、4.4 节。原 [PR Fast](https://github.com/SunSi12138/SharpLink/actions/runs/38170674766)、[Package Smoke](https://github.com/SunSi12138/SharpLink/actions/runs/38170674729)、[CodeQL](https://github.com/SunSi12138/SharpLink/actions/runs/38170674727) 均为 success。原 allocation 根因仍未确定。
- #851：PR 回归的指定 old 1 red、current 四轮 focused 与四轮 2083 全过，见第 5.3 节。原 [PR Fast](https://github.com/SunSi12138/SharpLink/actions/runs/38170896575)、[Package Smoke](https://github.com/SunSi12138/SharpLink/actions/runs/38170896580)、[CodeQL](https://github.com/SunSi12138/SharpLink/actions/runs/38170896598) 和 [PR 独立回归](https://github.com/SunSi12138/SharpLink/actions/runs/38170896610) 均为 success。
- 共同正确性控制 `c7ba1768`：原始指定 old 3 red、current 四轮 focused 与四轮 2085 均已核验通过，见第 6 节。它没有把 #849 的八个 hook case 暗算进这三个 focused case。
- 初始 B2 `d472eee7`：已实际完成编译和 focused，唯一 deadline 退款失败保留；四轮 full 和后续检查为 NotRun。它的修复集成是另一个待验证候选，不由以上三个独立 PR 的 green 自动代替。

相关研究边界见 [历史失败及证据审计](failures-and-evidence.zh-CN.md)、[成熟框架与候选架构](frameworks-and-candidates.zh-CN.md)、[D 的测量方法和控制](directional-control-and-methods.zh-CN.md)。本文件只负责正确性发现及其独立验收，不改变那些报告各自冻结的测量群体或架构判断。
