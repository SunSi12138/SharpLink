# 3.0 技术决策树：先保留合同，再按证据选下一步

记录时间：2026-10-28，CST。本文是后续执行设计，不是新的性能验收。只读核对了下列源码与留存报告；本次未修改产品、运行性能实验或改动远端。后续 owner 先读取同目录最新交接状态，再采用本树；已完成的实验不重跑。

**云端接手更新（2026-10-28 11:18 UTC）：**默认 PGO Client-only run40646151477 已[独立复核](reviews/931-pgo-40646151477/INDEPENDENT-REVIEW.md)，结论 inconclusive，原 #931 全产品 NoGo 保留；实际 TCP Tier1 重复测试支持 §1.3 的 R1-only 非计时机制步骤。#933 run40644131003 当前头10b 的 canonical 已核实成功，原始 companion/功能性能仍待审核。以下历史分析和失败不被本更新覆盖。

**推荐顺序：先消费 #931 单端消融与 #732 三臂实验；#933 独立审查/当前头 CI 可同时推进。只有证据指向的一个产品变量进入下一候选。** 无结论就保留 No-Go，不把订阅截止时间变成发布条件。

## 0. 源码身份和不变的验收含义

| 对象 | 可复查输入 | 当前结论 |
|---|---|---|
| 集成基线 | 主目录 `dev`：`eec9524aa21251a1d548f33e363dd4c51094cb14`；`progress.json` | 仍有三项决策及最终发布门禁 |
| #931 | `/Users/sunsi/.codex/worktrees/physical-ownership-931/SharpLink`，`ac17c8d8`；正式对照基线 `cc7c458b` | correctness/source 通过；run `40634900826` 性能 No-Go |
| #732 O/A/B | O/pre-M=`a6bd0352`；A/M0=`37548b7a`；B/M1 **生产改动**=`652ffca7` | M0 No-Go；M1 本地实验 inconclusive |
| #732 计划/报告 | `/Users/sunsi/.codex/worktrees/issue-732-method-facts-m1/SharpLink`；计划修正 `2756e881`，harness `e0e0abf2`，报告 `3c36ab49` | `2756e881` 不是 M1 产品补丁；不要按报告 HEAD 误判被测产品 |
| #933 | `/Users/sunsi/.codex/worktrees/maintenance-933-integration/SharpLink`，修复 `50e608ca`，此前失败头 `16efc987` | 限定的 implementation-only maintenance；完整 #775 仍未实现 |
| #775 历史 | Git 对象 `0a09b05f` 下 `eng/issue775/FINAL-FINDINGS.md`、`README.md`、`BOUNDARY-PERF.md`、`PERSISTENT.md` | 原始 dev 性能恢复未成立；旧 #861/#862 不因此获得集成许可 |

表内短 SHA 由已有仓库对象解析。各 worktree 的 `src/`、`test/`、`doc/` 路径以下相对于该行根目录；不是主目录下可能较旧的同名文件。每个新候选都保存实际生产 diff、完整 SHA、编译/运行身份、固定实验计划和失败记录。工具 owner 已在准备现有工作流；不要另起性能平台。

判断必须区分三层：**代码机制改变 → 特定实验信号 → 3.0 发布接受**。前两层不能自动推出第三层。既有工具没有给出新的等价界限；不能临时添加“3% 内算过”、把不显著写成等价，或套用 2.0.3 的特定例外。最终冻结头仍须执行正式 release gates。

## 1. #931：先确认 Client 贡献，再最小化迭代器成功路径

### 1.1 已证实与未证实

正式 48-process run `40634900826`：dynamic100 吞吐 −2.67%、CPU/op +2.85%、P99 +38.08%、P99.9 +21.64%，六对 P99 全部不利。原始数据及 `mechanism-analysis.json` 位于主目录 `artifacts/release-readiness/2026-10-28/ownership-931-run40634900826/`。

已观察到 `InterceptedAsyncEnumerable<int>` 的 EH 4→7、MoveNext IL 1514→1920、Tier1 body 4280→4834、`sub rsp` 272→288，成功循环多跳转/重复 `hasNext` 测试。**这还不是根因证明**：FullOpts 帧和 dynamic 外层某些尺寸反而缩小；scalar 的闭合泛型也不等同实际 TCP 请求类型。private .NET 10.0.13、默认 PGO 和 CPU affinity 已对称，仍不能从尺寸推出 CPU 或尾延迟比例。

### 1.2 现在先读正在进行的有界消融，不同时改产品

工具 owner 路径：`/Users/sunsi/.codex/worktrees/physical-ownership-performance-tools/SharpLink`，`eng/physical-ownership-probes/README.md` 的 client-only ablation。两臂都使用候选 Runtime/Server，只替换 old/new Client；反向混装不合法，因为新 Client 依赖旧 Runtime 不具备的 helper。

固定 dynamic intercepted TCP、100 items、1 worker/1 ready connection、FullOpts、private 10.0.13、同两逻辑 CPU，先两臂真实 TCP native preflight，再 AB/BA 六对共 12 进程。preflight 必须捕获实际闭合泛型的 intercepted iterator、dynamic iterator、Runtime outbound pump、Server bridge 四个 FullOpts body。完整性失败先修工具/环境，不解释性能。

按结果选择：

1. **同公共 Runtime/Server 下，new Client 仍一致不利**：确认该混装条件下 Client 差异贡献，进入下述 R1；仍未定位到某条 EH/跳转。
2. **FullOpts 下差异消失或方向翻转**：原实验同时改变多端源码，而本实验固定 Runtime/Server 又关闭 tiering；因此还无法区分 Client 的 tiering 敏感性、Runtime/Server 主效应或跨端交互。它不证明 EH 无害，也不证明候选恢复。优先对已留存原 Tier1 和真实 TCP body 的循环/分支作局部对照；如需新测量，只审批一次与本消融同场景的默认 tiering Client-only 对照。不得把关闭 tiering 写入产品作为“修复”。
3. **噪声、失败或方向混合**：保留 No-Go；先报告哪些过程完整、哪一对照未能区分解释。不追加有利样本，不并行尝试多种产品优化后挑胜者。

### 1.3 主方案 R1：已实现的最小出口实验，尚无性能接受

产品提交 `94adeae886c4ddfd0c02ec2dcb700cd97e9cad80`，仅报告后续 `87f76fb805c70693513a87c5acec03e9643e910a`。已独立审核，67 Unit/5 Integration 通过；非计时 ARM64 FullOpts 的重复测试消失，IL1920→1882、native5348→5308，EH/对象/栈/分配未改善。见 [完整实验报告](loop-exit-experiment.md)。当前仍不准合并。

**触发：**Client-only 结果或实际 TCP native 对照支持继续检查 client 成功循环。**最小编辑：**`src/SharpLink.Client/SharpLinkClient.Interceptors.cs`，`InterceptedAsyncEnumerable<T>.GetAsyncEnumerator` 的 `while` 内部。

当前代码在内层 `try/catch` 中计算 `hasNext`，用三元表达式读取 `Current`，再以 `hasNext && ...` 判空；离开 catch 后再次测试 `hasNext`。候选只把结束出口提前到同一 `try`，保留其余等待/异常代码逐字不变：

```csharp
while (true)
{
    T item;
    try
    {
        // 原样保留 deadline 检查、MoveNext 和三条等待分支。
        // 这里已得到 hasNext。
        if (!hasNext)
            break;
        item = enumerator.Current;
        if (!responseNullable && default(T) is null && item is null)
            throw new InvalidCastException("A non-nullable intercepted RPC stream response was null.");
    }
    // 原样保留两个 catch、filter、ownershipTransferred 和终止异常创建。
    yield return item;
}
```

这是设计片段，不是完整可粘贴替换函数。C# 限制的关键是：`yield return` 仍在有 catch 的内层 `try` **外面**；外层仅 `try/finally`，允许 yield。内层普通 `break` 退出 `while`，会经过同一个外层 `finally`。不要把整个循环和 yield 包进 `try/catch`，也不要在 catch 中 yield。编译器接受只是首个检查；生成状态机未必因此更小，必须检查实际生成结果。

预期机制仅为减少成功路径的条件/默认赋值；EH 数可能不变。`Current` 和非空校验仍受原 catch 覆盖，false 时仍不读取 `Current`，deadline 的读取次数与相对位置不变。不要顺手移动正常 Dispose、CTS 或 dynamic lease。

**最小反例：**空流的 `Current` getter 必须从不执行；一项流恰好读一次；`Current` 抛出已有 physical carrier 时仍转移 ownership；正常非关联应用异常仍保持原实例和原 Dispose 覆盖优先级。再覆盖异步 pending move 到期、同步成功但 deadline 刚过期、提前停止枚举。

**验收顺序：**构建 + 现有所有 ownership/异常语义回归 → actual TCP closed-generic native/布局比较 → 对冻结新候选执行原口径性能接受。若 native 未改变目标分支形态或性能仍不利，记录并撤回这个单独候选，转 R2 或保留 No-Go；不得把 EH/IL 数量下降当作通过。

### 1.4 备选 R2：仅把异常 ownership 创建体移到冷 helper

**触发：**R1 无效，但实际 body 显示异常构造/owner 注册/Track 放大热函数，并有明确的抽取边界。**编辑：**仍限同文件/嵌套类，新增私有 static、候选可标 `NoInlining` 的同步 helper；不抽取每项 `MoveNextAsync` 为新 async helper。

helper 接受现成 `pendingMove`、enumerator、CTS 和原异常，创建原来同类别的 terminal、`CreateCarrierOwner`/`CreateOwner`，调用现有 `CompleteAbandonedStreamAsync` 并 `Track`，最后**返回 terminal 供原 catch/分支 throw**。它不捕获 `this`、codec、dynamic module、stub 或 delegate。取消与 deadline 保持不同工厂：普通 deadline 创建 owner；由已有异常转来的 deadline 用 carrier；取消保留 OCE/TCE 分类、message、token 和原始 logical exception。

必须保留的顺序：

1. 调用点先置 `ownershipTransferred = true`，再做原来的终止异常/owner 创建和 Track；不要到 helper 返回才置位。内层取消重新抛出的 terminal 不应被外层 filter 二次接管。
2. association 注册先于调度/Track；`CompleteAbandonedStreamAsync` 起始 `ForceYielding` 不动，取消回调不跑在公开失败返回栈上。
3. pending `MoveNext` 结束并等待其 descendant 后，才 `DisposeAsync`；两者绝不并发。Dispose 的 descendant 也加入同一个 owner 图。
4. lifetime CTS 在整个 physical owner group 完成前不能 Dispose；外层 dynamic `TransferAbandonedOwnership` 的 `cancelLifetime: false`、lease 转移、清空局部 lease/combined 保持不变。
5. 未关联的应用异常继续 `throw;`。不要统一包成新异常，不要用“抛原异常”替换框架 carrier；mapper 依赖 `GetLogicalException` 返回原应用实例。

该方案主要减少 iterator body 中的冷指令，**不会自动减少 catch/EH 数**；没有成功路径 new owner/closure/Task，也不允许新增全局强字典。保留 terminal、enumerator 或完成 Task 后的动态 ALC 回收是验收条件。

**最小反例/复用用例：**`AbandonedClientOwnershipTests.StreamTransfersSequentialCleanupBeforePublicTerminal`、`DeadlineDoesNotRunBlockingCancellationCallbackInline`、`ReusedPublicTerminalMustNotMakeCleanupWaitForItself`、`RecycledCrossCallTerminalsRetainBothModulesUntilBothDisposalsFinish`；`AbandonedClientNestedOwnershipTests.IteratorBoundaryTerminalTransfersLifetimeWithoutDelayingPublicFailure`、`UnassociatedApplicationFailurePreservesIdentity`；`AbandonedClientApplicationTerminalTests`；`AbandonedPhysicalWorkCycleTests`；integration 的 `RuntimeAssemblyIntegrationTests.PhysicalOwnership.cs` 与 `QueuedPhysicalOwnership.cs`。R1 同样复用这些现有检查。

若 helper 引入额外每项 await/state、成功路径分配，或不能保留上述转移顺序，停止该设计。R1/R2 均失败后的推荐是**保持 #931 No-Go，并由 release owner 决定继续有界研究还是调整发布时间**；不能退回会提前释放模块/CTS 的旧语义换取数字。

## 2. #732：完整 facts-once 不拆；先选 M1，再考虑去重表示

### 2.1 当前事实和完整合同

M0 对 pre-M 的历史主要坏格：TCP/interceptor/declared timed/one-item/c1，mean GM 1.145，P99 GM 1.668。M1 仅把 `RpcMethodFacts` 表示改为 readonly descriptor + known/cancellation byte：冷对象 48→56 B、`ToDescriptor` IL 85→26；不能把约纳秒级投影差异直接解释成整个 RPC 的微秒级回退。

本地 16-process/640k campaign 按固定规则 T/U 都 inconclusive。I/T mean GM 0.420119，同时无 interceptor P/T 0.414285；T interaction GM 1.014084，并有外部项目编译和虚拟机重载。因此“快了 58%”不是可用产品结论。已完成的 GitHub run40642190610 三臂 O/A/B、36 process、18 pairs、1.44M calls 及同源 controls 的六项 T/U 判断均为 inconclusive；具体复算与阶段漂移见 method-facts-variance.md；以该 owner 最终冻结计划为准，不拼接本地数据增加样本量。

**任何路线必须同时保留：**

- `SharpLinkServer.ResolveMethodCall` 每次调用取得一次完整 facts，验证 known/unknown/invalid 与 contract/method identity；全部五类 RPC 都适用，异常 mapper/telemetry/stream continuation 不重查当前注册。
- `ResolvedMethodCall` 仅在持有原 registration 的活动 dispatch 使用；排队保存 `QueuedServiceSelection` 的 weak identity + host-owned scalar facts，恢复时拿原代 lease，再解码/调用；不得强引用原 stub 保活 ALC，也不得绑定最新 registration。
- full timeout presence + nullable value/full ticks、0–127 stream count、cancel、payload/nullability、idempotency 无损。`UnknownMethod` 不是伪造 Unary。
- untimed synchronous static stream 不因 facts 单独租 call state；已异步悬挂的调用可能依原合同需要 state。`CanBeCanceled` 不是“存在请求级 state”的证据。
- 生成 API 数字仍 5；完整 facts 身份 `sharplink-3.0-api5-complete-method-facts-v1`，旧开发 API5 identity 拒绝。wire 2.4、method IDs 不变。handwritten stubs 同样履行完整 resolver 合同。

### 2.2 主方案 F1：用三臂结果选择现成 M1，不叠优化

1. **B 相对 A 有 interceptor-specific 信号，且 B 相对 O 的原坏格也恢复到已批准接受条件：**采用现有 `652ffca7` 表示；随后在整合 #931/#933 的实际候选上做全部 correctness、ABI/API、allocation 与性能接受。B/A 只回答改善 M0，不能替代 B/O。
2. **B/A 改善但 B/O 仍不利：**保留机制收益与 release No-Go 两个结论；进入 F2 的前提是有上下文存储/复制证据，不能立即宣布 #732 完成。
3. **A、B 都类似不利，或 P/T 与 I/T 一起变动、controls 超过效应：**descriptor cache 不是已确认解决点。先检查按完整计划保留的各 pair/controls 与实际 loaded assembly/state-token preflight；无可区分证据就保持 No-Go，不挑选 U、平均数或另一平台替代主要 T。
4. **A/O 与 B/O 都满足接受，B/A 没有可靠优势：**不为 85→26 IL 自动选择 B；比较冷体积与实际观察者成本，优先较小、最简单的已通过表示。若采用 M0，必须是新的有效 O/A 证据确实改变了原 No-Go 判断，而不是忘记历史失败。

`doc/generated-method-facts-3.0-migration.md` 仍写“四个 readonly fields”，M1 源码实际两字段。最终采用 M1 时更正这条描述；不要为文字修改重跑仍适用的候选证据。

### 2.3 备选 F2：消除 server invocation context 的重复 descriptor 存储

**触发：**三臂结果仍显示 interceptor 特有成本，且源/native/分配证据指向 `SharpLinkServerInvocationContext` 的每-call构造/复制。**这是未验证设计，不是 M1 的现有收益。**

源码 `src/SharpLink.Abstractions/SharpLinkInterceptors.cs` 的 server context 构造器当前同时存 `Method = method.ToDescriptor()` 与 `MethodFacts = method`，后者供 interceptor terminal 调用原 stub。M1 已有唯一完整 immutable descriptor，因此可以单独试验删除 context 的 private descriptor backing field，把公开只读 `Method` getter 改为从原 `MethodFacts.ToDescriptor()` 返回 value copy。

实施边界：保留 `MethodFacts` 原对象、不按 ID 重新 resolve；保留构造阶段对 unknown 的失败时机与异常类别/文案，可把现有 known 校验提成不分配的内部方法供构造器和 `ToDescriptor` 共用。不要仅因 getter 延后而把本来构造时抛错改为 observer 首次读取才失败。公开 `Method` 类型/getter 及返回独立值副本合同不变；Client context 不做此改动。

**待验证机制：**从每个 server context 去掉一个 32-byte 值存储，并省掉无观察者读取时的投影；实际对象分配大小/字段偏移由 CLR 测量，不能预先宣称精确少 32 B/op。每次 getter 仍有 known 检查和值复制，多次读取可能抵销收益；保留“只调用 next，不读 Method”和“连续读 Method”的匹配用法，不据此更改原基准服务。

**最小反例：**interceptor 连读 descriptor 并修改返回副本不能改变后续 getter；挂起 stream 后替换/移除 registry，mapper 仍看到原 timeout/idempotency；保留 context/facts 时旧 collectible generation 可回收；queued continuation 不换 registration；空/unknown facts 构造时仍拒绝。复用 `CompleteMethodFactsDispatchTests`、`GeneratedCompleteMethodFactsTests`、`RpcMethodFactsTests`、`RuntimeAssemblyIntegrationTests.MethodFacts.cs` 的全 scope 覆盖，不只测一条被优化路径。

若实际 allocation/native 没有目标变化，或原 T 格/P99 不接受，撤回这一单独变更，保持 No-Go。不要为消除 context 多分配去改 call-state pooling；该池实验已被拒绝，且 token/state association 是独立合同。

### 2.4 备选 F3：保持 No-Go，而不是伪装成 generated fast path 完成

`RpcGenerator.StubEmitter.cs` 已生成每 method 一份静态 facts、一次 resolver switch；生成的 nullability/codec/业务目标本来就是常量。再提“把 nullability 改常量”没有新工作可做。若未来 native 明确显示 generated dispatch 本身为瓶颈，才能提出一个独立、有固定证据目标的常量传播候选；当前不建议新建 Binding、dispatch-index ABI 或 per-method 泛型框架。

尤其禁止只给 Unary/无 interceptor 直达路径减少 lookup，再把其他 queued、OneWay、stream、mapper、telemetry 恢复为旧多次查询，宣称完成 #732。完整 facts-once 是本需求，局部 fast path 只是潜在优化。缺少完整方案时，release owner 应显式保留 #732 blocker/调整发布范围，而不是静默删去该要求。

## 3. #933 / #775：让限定维护先被验证，完整 Binding 单独承担证据

### 3.1 已证实的边界

#933 仅支持 host-authorized、全 server、认证 TLS 固定 cohort、兼容 implementation-only 替换、固定 endpoint、永久封闭 runtime/catalog 配置。当前、retained 或正在 physical reclaim 的 waiting-capable admission generation 都不能 opt in；不能以“当前队列为空”代替。Wire 2.4 capability bit 7/frame 17/144-byte stage 1–11 不变。

`50e608ca` 修复前，run `40636189267` 的 unit 2435、generator 294、load 77 通过；integration 530 pass/26 fail。单独反例证明：无关 manifest retirement 会触发 publication revision 冲突；fixture 的失败清理会留下全局 catalog replacement manifest。修复后的本地 Unit 2439/Integration 557 是新头证据，hosted 当前头尚待确认；不要把两个缺陷仅因日志相邻就写成全部失败因果链。

### 3.2 主方案 M1：验收现有 retirement-only 修复与限定维护

**编辑 owner：**现有 maintenance integration owner；本树不重复其审核。核心函数 `SharpLinkRuntimeContext.PublishGeneratedCodecs` / `CanPublishAfterManifestRemoval`，以及 queued fixture 的 `finally` cleanup。

修复的允许条件必须作为一个整体维持：当前 owner set 是已验证 set 的严格子集；revision 增量恰等于移除 owner 数；当前 published codec snapshot 与捕获的旧 map 为同一对象；candidate 不复活被移除 owner。依赖验证仍要求 exact Type、owner assembly、非空且匹配 codec hash；同 assembly 的另一 surviving owner 可以供依赖，但不同 assembly generation 不行。并发 adopt/competing publication 仍拒绝。

metadata 是 public SPI。读取/验证 metadata 在 `_registrationGate` 外，锁内只对捕获身份及内部集合判断；不得用大锁包住 getter“修好竞态”。reader-prefix、intrinsic-send、`PreparedImplementationReplacement` 和 module physical ownership 不因该修复改合同。

**当前最小接受组：**四个新增 codec retirement/competing-publication 用例，既有 referenced-codec owner/hash/adopt negatives；`QueuedFixtureFailureMustRemoveReplacementManifestFromCatalog` 要保留原失败实例、catalog 清除、下一 server 可建。完整当前头 hosted inventory 必须保持 2439 regular unit（2444 discovery 含 5 Explicit）、557 integration；不是降低 collector 预期跳过失败。

历史 native 10.0.2 `OnRegistered` primary Tier1 缺失的处理是同源、对称 shapes-only companion；当前头10b的[原始证据审计](reviews/933-canonical-40644131003/AUDIT.md)确认10.0.2 primary gap为空，而10.0.13 baseline OnRegistered PGO Tier1 primary gap保留，两runtime的对称companion均完整。primary 缺失继续列出，不能拿 Tier0 或另一版本顶替。companion 两臂完整 Tier1 是其 gate；其 allocation window 与 primary 分开。10.0.2/10.0.13 均需实际 Linux x64 证据。producer Unary/Retry +16-byte PGO frame 与 stream-entry 成本仍需最终候选的接受，不由“无新 per-call table”抹除。

若上述当前头 gates 通过，只推进限定 #933，**不关闭完整 #775**。若失败属于 actual product correctness，先重现一个最小失败并返回原 owner；若 native inventory 不完整，则补既定 capture 缺口，不篡改 reader/producer门槛。并发 adoption 真正改变 catalog 应保持冲突失败，不能继续扩大 retirement allowance。

### 3.3 备选 M2：完整 Binding/receipt 路线，先冻结合同再分阶段实现

**触发：**用户要求 3.0 必须含完整 #775，或固定 cohort/implementation-only 无法满足真实用途。此时 #933 是可复用的子能力，不能代替目标；release owner 需把完整 Binding 作为未完成的大项继续，而非给 #933 改名。以下是未来设计顺序，不是批准恢复历史实验或立即发布协议。

**历史负证据约束：**

- `0a09b05f:eng/issue775/FINAL-FINDINGS.md` 明确：静态 Unary/OneWay 跑通，原始 dev 基线恢复未证明，#862 不合并、#775 不关闭。最终构建诊断中部分 DLL 差异仅在标识/调试区域；它不能解释旧性能差异，更不能抵销原性能失败。
- `BOUNDARY-PERF.md` 的某候选对原 dev OneWay sender +3.00%/+5.07%，对旧静态 SharedMemory OneWay c8/p4 receiver −12.88%/CPU +15.17%；分属不同同期对照，不能互相相除，也不能只选 old-static 的改善作为原始基线恢复。
- `PERSISTENT.md` 的真实缺陷：`AwaitPersistentDecodeAndContinueAsync` 重读解压后 envelope 丢失已验证 BoundMethod。修复保存并恢复**同一引用**，不重查当前 registry。未来含 queued/persistent decoding 的身份必须贯穿恢复边界。
- 老静态原型只覆盖固定 endpoint、Unary/OneWay，无动态 Replace/Unregister、receipt、stream、cluster、重连 prepare、slot 回收。实验 magic 0xB7、控制帧 240/241 不是已分配生产协议；64 binding/256 method/固定 incarnation=1 也不是完整动态 ABA 方案。
- #861 的 per-call identity-header 历史实现与 #862 static Binding 都不在当前 dev 验收范围。它们只能作为设计/反例资料；不直接 cherry-pick 协议面、旧公共 API 或原有 `src` 全树。

**阶段 A：先规定身份和失败边界（无需写新协议代码）。**

1. 在实际数据 Session 上完成 BindContract；完整 contract/codec generation identity 进入冷验证，返回 binding token 与 exact registration identity。Client 的本地 `Get<T>()` 不得偷偷等待网络；显式 preparation 有 deadline、bounded capacity，单 waiter 取消不取消共同 bind operation。
2. 冻结 token 的 session scope、slot/incarnation 位宽、回收、耗尽与 ABA 规则；如选择 session 内不回收，耗尽时显式失败/重建 session，不能回绕复用。重连必须重新 bind，不能复用旧 session token。
3. Client 选路必须原子选择 `(connection, binding)`，重试换 connection 重新获得对应 binding；不得带旧 token 重定向。Server 在解压/业务 codec 前验证完整 token/method/shape，再获取 exact registration 生命周期；不回退到 ContractId/latest registration。
4. 精确定义 receipt 证明什么：登记可见、禁止新调用、逻辑排空、物理用户工作/Dispose 完成、恢复可用是不同阶段。receipt 含 operation、endpoint/session incarnation、exact registration 与适用域；控制面 inventory 的 epoch/revision 或 `CommittedPaused` 不可充当 ExactRegistration。

**阶段 B：一次只贯通一个真实端到端身份路径。**

入口位于现有 `SharpLinkClient.ContractBinding.cs`/连接选路与 retry 文件，Server `ServerRequestEnvelopeReader.cs`、`SharpLinkServer.RequestEnvelope.cs`、admission、persistent-decode continuations、`ServiceRegistration`；协议扩展应独立于已稳定 maintenance frame。先写身份错误在 codec 前失败、队列恢复仍持原身份、persistent 解码不丢身份的反例，再实现最小路径。新 receipt 数据只持 host scalar IDs，不让保留 receipt 根住动态 Assembly/Type/stub。

完成静态路径后，下一阶段才连通 dynamic replacement 与 streaming：复用 #931 的 physical owner 完成条件、#930 weak queued generation、#933 irreversible seal/commit-paused/resume 顺序；不借用普通 `ReplaceAssemblyAsync` 的发布后排空结果当作全局 quiescence。动态 client contract/codec replacement 需要 exact owning hash + isolated candidate dependency graph，本地 implementation-only transaction 不能放宽为 general codec graph 替换。

**最小反例集合：**两个 session 同数值 token 不串用；错误 incarnation/已回收 slot/旧 receipt拒绝；并发 prepare 只发布一个 binding，单 waiter取消不毁共享结果；disconnect/reconnect/选路变化不携旧 token；queued call 与 compressed persistent continuation 在替换后仍用原身份；取消/timeout 已公开返回而 MoveNext/Dispose仍运行时，不发“physical completed”；持有完成 receipt 后旧 ALC 可收集；恶意 shape、未知method在任何业务反序列化前失败。

**阶段 C：接受覆盖完整范围。** 新实现先固定其实际父级 dev、候选及适用的发布基线；不要把历史 old-static 重新命名成原始 dev。历史 `1a54993d` 对照与新基线对照分别报告，后者不会改写前者的失败。保留 sender queued-completion 和 receiver business-completion 两条 OneWay 计数，首 bind/首调另列；TCP/SharedMemory、Unary/OneWay/三种 stream、静态/动态、队列/压缩/重连及 AOT/Trim 需要对应验证。先决机制候选若仍过不了预定成本/生命周期条件，不扩大协议范围。任何局部静态成功都不能关闭 #775。

### 3.4 备选 M3：延期受阻能力，保留正常运行入口

如果 #933 仍不能通过 correctness/成本门禁，且用户接受不把该能力包含在本次发布，则由 release owner 明确调整 scope，继续发布既有已接受能力，并让 #775/#933 保留开放。现有普通 register/replace/unregister 的原合同、正常 queued admission、既有 codec providers 必须可用；不能要求所有用户采用 sealed catalog 或禁用队列来满足新维护功能。

若用户未同意删除原交付目标，默认动作是延期发布而非静默删功能。失败的优化和研究分支保留可复查源码/数据，不回退公共 identity/资源寿命去换“绿灯”。

## 4. 后续 AI 每次只作一个决策

1. 读取各 owner 的最新 outcome 和实际候选，先分清工具成功、实验信号、release接受。
2. 按本树的触发条件选一个候选；没有触发证据就不改产品。
3. 在现有 owner/worktree 内做最小 diff，复用列出的生命周期/身份反例；相同输入的有效证据不重复跑。
4. 原门禁不通过就保留失败与 blocker，回到该节点明确的下一步；不要新增平台、偷换基线或把范围缩成好测的 fast path。
5. 只有 root/release owner 汇总 final exact-head correctness、性能、API/ABI、package/AOT、soak 及发布授权后，才推进正式发布。本文各路线本身都不构成该批准。
