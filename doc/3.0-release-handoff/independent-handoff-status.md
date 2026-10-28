# 独立交接更新：2026-10-28 14:24 UTC（原 GitHub 快照 14:06；#962 关闭于 14:19、#961 当前头/诊断于 14:24 再核实）

## 先看结论与保存边界

**尚未达到 3.0 发布条件，也没有相对官方 2.0.3 的 QPS +10% / P99 −20% 验收结果。** 交接仅保留在 `codex/3-release-handoff`，证据仅保留在独立证据分支；不创建交接 PR，不把交接资料再次并入 dev。本页是有截止时间的事实快照，后续进展须核对精确 HEAD；文末旧快照原样保留。

**用户最新范围决定：3.x 是破坏性升级，允许 API/ABI/wire 变化，不要求 2.x↔3.x 互通。#962 不采纳，14:18:45 UTC 已关闭且未合并；跨版本互通门禁不再是发布 blocker。仍保证 3.x 自身正确性、完整功能、资源/取消/错误/协议正确性与迁移说明；breaking 不是功能缩水许可，真实功能取舍仍须讨论。官方 2.0.3 继续作为性能基线。**

只读 GitHub 核实：dev 为 [`e3808b65e694bf1ed250d55fa2ad48a6ef88d285`](https://github.com/SunSi12138/SharpLink/commit/e3808b65e694bf1ed250d55fa2ad48a6ef88d285)，tree `dd92a580a0a0b934a6571a54d0194c02e7a782d2`；完整 recursive tree 未截断，仍没有 `HANDOFF.md` 或 `doc/3.0-release-handoff/`。本次更新的远端父提交是 `98ea64cab08190615e7022d5964fae40ec6c5bff`，不改变原始 [ba99 入口](https://github.com/SunSi12138/SharpLink/blob/ba99c6f9467fd85233afbc0dc1c84cf839e1b5a0/HANDOFF.md)。[只读远端观察记录](reviews/checkpoint-20261028-1405/github-observations.json) 区分了成功、取消、跳过与失败，不用 workflow success 代替原始工件审核。

## 当前产品与门禁

| 项目 | 已建立的事实 | 未通过、未运行或待审核的边界 |
|---|---|---|
| [PR #961：故障终止输出清理](https://github.com/SunSi12138/SharpLink/pull/961) | HEAD `1a76e1b858b9b306f8d09ed6f85bc3c5319e2da5`；产品/census 与新增 nonce 工具 `555cdb9f8e7230b555257ddc86ce8fcb4dc0181d` 的独立 source/remote 审核 PASS。17 个新增测试在 10.0.2 / 10.0.13 均通过，133 个相邻生命周期/所有权测试及 489 文件架构检查通过。 | PR open、未合并。新 hosted nonce 诊断原始证据独立 PASS；下一步新源码 Extended。旧 Extended 失败原样保留，不能把狭窄诊断当完整 suite 通过。 |
| [PR #962：真实版本 wire 门禁](https://github.com/SunSi12138/SharpLink/pull/962) | HEAD `ba4fe43c40621c91deedfaadbbdbd3e70f5b78d1`；本地 controller、CI wiring 和精确远端 tree 审核 PASS。Linux hosted [40698598415](https://github.com/SunSi12138/SharpLink/actions/runs/40698598415) success；Mobile Wiring [40698356754](https://github.com/SunSi12138/SharpLink/actions/runs/40698356754) 于 14:01:21 UTC success。 | Hosted 原始最终证据独立审核 PASS：74 文件、9 对进程、18 个 exit 0 及官方 artifact 身份全部核验。所有适用 CI 已成功。14:07 UTC 的合并询问已由用户最新范围决定取代：**不采纳；14:18:45 UTC 已关闭、未合并，不再待确认，不再是发布 blocker**。14:19 UTC 只读核实 head 仍为 ba4fe43；分支和 artifacts 保留，历史 PASS/FAIL 不改写。Mobile workflow success 不把 Linux wire 结果升级成跨平台 wire 验收。 |
| #931 / R1 | 新全产品诊断工件完整，独立重算确认 dynamic1 尾延迟不利，见下节。 | 原 NoGo 不变；没有性能推广或重跑刷绿的理由。 |
| #732 / #933 / #775 | #732 M0 NoGo / M1 Hold；#933 已有当前头 correctness/native 审计。 | #933 feature performance 及 #931 前置仍开放；受限 maintenance 不能作为完整 #775 的完成证明。 |

**#961 必须保留的失败与下一步。** 原始基线 14 项中 7 pass / 7 fail。初版三个严格 census 门禁因新增 17 项未更新预期数量而失败，原始 C# 报告实际零失败；修正后 [Send Credit Lease Race 40698378662](https://github.com/SunSi12138/SharpLink/actions/runs/40698378662)、[Generated Stream Snapshot Ownership 40698378679](https://github.com/SunSi12138/SharpLink/actions/runs/40698378679)、[Send Credit Terminal Accounting 40698378900](https://github.com/SunSi12138/SharpLink/actions/runs/40698378900) 均 success，严格计数/零失败/零跳过/负例没有放宽。另一个 [旧 PR Extended 40697027111](https://github.com/SunSi12138/SharpLink/actions/runs/40697027111) 是真实失败：`SharedMemoryHandshakeShouldRejectMismatchedAcknowledgementNonceAndCleanMapping`，490/491 通过，后续合法客户端 hello 遇到 Broken pipe。诊断区分了「mapping 已删除」与「第二次 accept 已 ready」。工具 `555cdb9f8e7230b555257ddc86ce8fcb4dc0181d` 已独立 source/remote PASS；当前 HEAD 上的 [hosted40704347751](https://github.com/SunSi12138/SharpLink/actions/runs/40704347751) completed/success，其原始 artifact `12822822111` 的 51 文件已独立复核 PASS：两个 gated 正例与增强 fixture 成功，删除 nonce 检查、阻止 rearm 两个受控负例均按预期失败。原始工件位于当前环境 `pr961-ci-artifacts/12822822111/`；负例失败是验证敏感性的有效证据，不应改写成产品测试成功。保留本地 NamedPipe Permission denied，不能将其称为产品断言失败，也不重复被拒绝路径。下一步针对新源码运行并审核 Extended，不能用旧头 census 或本次狭窄诊断替代；不是原样重跑旧失败刷绿。

**#962 的实际范围。** [独立 raw 审核报告](reviews/checkpoint-20261028-1405/wire962-independent-review.txt) 已核对原文件 SHA-256 `0cb45c18342b38eadb04f1d5644c28473129c7c8c55455a67a9721847623b640`，复制未改写；其中 `verify.py`、`output.log`、`report.json` 仍在当前环境的 `sharplink-wire-gates-20261028/artifacts/wire-ci-40698598415/independent-review/`，并非本交接目录内的文件。Hosted owner 记录及最终独立审核一致：74 个文件、9 对独立真实进程、18 个 exit 0；Requests 为 `[5,5,5,5,5,0,0,0,0]`，实际 SDK 10.0.114 / runtime 10.0.14。正例保留全部五种 RPC，包含真实 NuGet 2.0.3 ↔ 3.0 双向；四个 hash 拒绝负例零业务 Request。各版本使用自己配套的 packaged Generator；进程内 API4/API5 不要求相同，但不变合同 hash 相同。本地 32 个工具回归测试通过。本地早期 relay ENOTCONN（7 对通过、第 8 对失败、第 9 对未运行）、restore 失败/中断，以及最初 Draft run [40698356919](https://github.com/SunSi12138/SharpLink/actions/runs/40698356919) skipped 均保留；skipped 不是测试通过。Hosted 原始 DLL/包/生成器/帧/退出码的独立最终审核已 PASS；这份技术事实不改变用户决定：#962 不采纳、不合并，不再请求确认或补做跨版本验证。当前 artifact 未上传 hosted 包二进制；独立审计核对的是原始日志内实际加载 hash、隔离包 provenance，以及之前独立核实的官方 NuGet hash，不能声称已重新取得并 hash 全部 hosted DLL 字节。此通过仍不得宣称完整 codec、AOT、安全、三平台或性能验收。

## R1：完整证据已持久保存，但结论仍是 NoGo

证据分支已只读核实到 [`f55d291f5fa1ee4c5807e4b448718e60405fd960`](https://github.com/SunSi12138/SharpLink/tree/f55d291f5fa1ee4c5807e4b448718e60405fd960)。[固定 README](https://github.com/SunSi12138/SharpLink/blob/f55d291f5fa1ee4c5807e4b448718e60405fd960/README.md)、[索引](https://github.com/SunSi12138/SharpLink/blob/f55d291f5fa1ee4c5807e4b448718e60405fd960/index.json) 和 [11-part manifest](https://github.com/SunSi12138/SharpLink/blob/f55d291f5fa1ee4c5807e4b448718e60405fd960/archives/r1-full-product-40687272444.parts/manifest.json) 是恢复入口。仓库保存的是有序 binary parts，**不是一个已存储的单体 tar.gz**；严格依 README 校验每片及重组整包，成功后才解压。

[独立发布后验证](reviews/checkpoint-20261028-1405/evidence-publication-verification.json) PASS：11/11 parts，611/611 members，重组 34,353,133 bytes，SHA-256 `892b8cfcfcc50da4cf22b4046ea0228ba31f2ecde04c783421760df3288ba23b`；远端 tree `d8ecfc13308e42eb59c0856e8fa15d629605a6ab` 与受审本地 `14aec5390a0b174d70cff53f3ecc781873ecae97` 相同，前 12 个归档逐字节未变。当前共 13 个逻辑归档；不能将「上传成功」称为「性能通过」。

[全产品 run40687272444](https://github.com/SunSi12138/SharpLink/actions/runs/40687272444) 使用固定工具 `271103147577a70bd973adfac1860f2da2276f07`，完整 baseline `cc7c458b559295e0fc61ec0062bb220faf880a76` 对 R1 `94adeae886c4ddfd0c02ec2dcb700cd97e9cad80`；48 个 TCP 进程、14 scalar records、2,763,672 ticks 有效。dynamic1 P99 六对全部不利，paired mean +7.06%，P99.9 +10.60%；其他 cells 混合/噪声较大，不构成等价或全面恢复。该报告与独立重算位于归档 `independent-review/REVIEW.md`。原 runtime 10.0.13 的结果独立保留，不和新的 10.0.14 机制实验混合。

早先 hosted native [40681832353](https://github.com/SunSi12138/SharpLink/actions/runs/40681832353) 已独立确认两个 4,352-RPC 进程、四个目标 Tier1 门禁完整；重复条件消除，但长跳仍在、body 增长。它只验证狭窄非计时机制，不消除之前本地四目标 gate Failed，也没有预言性能获益。

## 深层研究：不能相互替代证据

1. **阶段化 enumerator：正确性 PASS；实际性能诊断待执行。** 产品 `6077617167adbfd561e494d733fac2e1779f218f`，基线 `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`，机制工具 `4f2c1751fa7c6b47a2560984a863dd459b5678dc`。固定 8 个非计时进程全部 exit 0，但 4 scalar gate PASS、4 native completeness gate FAIL；193/193 receipt 文件经独立 hash 验证。机制结果混合：可见 FullOpts family 5,392 → 5,492 bytes、1,252 → 1,411 static instructions，尚未证明对象布局或 continuation 减少，缺失 native bodies 不能按零成本计算；这些数字不是 QPS/P99，也不能仅由代码增长判定无性能收益。[完整独立解释](reviews/checkpoint-20261028-1405/phase-mechanism-independent-review.md) 中的 STOP 建议原样保留为历史意见，**用户最新要求“代码变大无所谓，主要看性能是否提升”已覆盖当前停止决定**。Root 已承认此前倾向 STOP 过早，并启动 `measure_phase_rewrite_performance` 准备固定配对端到端性能诊断；本快照尚无 QPS/P99 数据、尚未执行该性能诊断。下一步独立审核冻结源码、工具、实际 runtime、负载、配对/样本与判据后测量，保留全部有利/不利结果，不追加样本刷绿；这既不更改旧 native completeness 失败，也不预先宣布收益或满足官方 2.0.3 +10/−20 目标。
2. **下一项 terminal-notification 表示：只有设计，没有实现/机制通过。** 方向是移除每个 pending item 的逻辑转发，保留终止、所有权、取消、deadline、异常与物理清理责任。约 136 B/item 斜率是待验证假设，尚不能归因给某个具体对象或 state machine。先独立评审结构、生命周期及有界方案，不能提前宣布省下分配或达到 +10/−20。
3. **Fenced wakeup：正确性与工具准备，不是收益。** 产品 `c17f434e8ec8d019625e64c7f51a4c8eb8e55981` 的独立 correctness review PASS；保留 full fence 与第二次读取，不是无栅栏优化。工具 `410c7a71e178385b5eaddf4b525af9e54dd78b4f` 的 20 项 offline tests PASS。10.0.14 准备阶段三个 Release builds 零 warning/error，D/C 各 7,685 文件 receipt 已独立 PASS（本地主报告 `/workspace/shared/sharplink-native-local-preparation-independent-review-410c7a7.md`）。实际环境是 **Debian13 Linux x64，不是冻结的 Ubuntu24.04 hosted 环境**；PASS 只针对 prepared-only provenance/build，不能当作 hosted matrix cell 或 probe/decoder 执行授权。这是 PE/module 的离线身份，不是已加载模块证据。零 probe、零 native collection、零性能计时；固定 16-process 方案尚未消耗任何 cell。正在准备 hosted prepare → 独立审 receipt → 使用同一二进制 collect 的方案；ARM64 尚未准备/审核，不能直接执行原 16-process 矩阵。保留第一次准备的 EROFS 失败，不用准备成功代替 Tier1、IL map、实际 caller/consumer 或 ARM64 验证。
4. **Borrowed singleton copy：原型 HOLD。** 原型 9 项为 6 pass / 3 fail，三项错误路径的对外行为差异保留。这促成单独的 #961 故障清理修复，不代表借用优化已正确或已合入。先量化仅少一次 copy/Rent/Return 的收益上限，再决定是否值得另立受审机制/计时方案；不作为 +10/−20 的主假设。

阶段化报告在本次交接中仅复制已有独立解释；原始 193 文件 corpus 仍在当前执行环境，尚未声称已上传证据分支。Wakeup 准备与 borrowed 原型的当前本地主来源分别为 `wakeup-native-preparation-20261028/REVIEW-ENTRY.md`、`receipts/review-index.json` 和 `sharplink-borrowed-singleton-20261028/research/borrowed-singleton/RESULT.md`。这些不是云端通用可下载 URL；恢复到其他机器前需单独审核并持久保存，不能把不存在的远端链接当证据。

## 接手者下一步

- #961 nonce 工具与 hosted 原始证据独立 PASS；下一步新源码 `1a76e1b858b9b306f8d09ed6f85bc3c5319e2da5` 的 Extended 验证，保留旧失败与受控负例；不把 census 或狭窄诊断通过误当全 suite 通过。
- 按 [当前门禁范围及精确待调整项](release-gates-and-merge.md#44-当前版本-abiwire-与-codec-正确性不要求跨-major-互通) 保留 3.x 自身 API/包、Trim/AOT、协议合法/非法输入、取消/资源/安全正确性；dev 现有误名 Cross-version 脚本实际为同版本 RPC，后续只应受审更正命名/范围，不盲删门禁。
- 保留 #962 精确 HEAD 的独立审核、适用 CI 和全部失败证据，但 #962 已关闭未合并，不把 2.x↔3.x 互通列为门禁，也不继续跑跨版本数据。发布验证转向 3.x 自身正确性、完整功能与迁移说明；同版本协议、跨平台/codec、安全及生命周期正确性仍按真实支持范围验证。
- wakeup 本地 D/C receipt 已独立 PASS，但仅 Debian13 prepared-only；完成 hosted prepare → 审 receipt → 同一二进制 collect 的独立受审方案后再进入适用的非计时矩阵，不能把本地准备充作 Ubuntu hosted cell；阶段化 607 按用户最新决定准备固定配对端到端性能诊断，不能仅凭代码大小停止；新 terminal-notification 仍先做设计评审。
- 继续以官方 2.0.3、相同实际 runtime/负载/功能范围制定独立受审的产品性能比较。当前没有任何结果满足整体 QPS +10% / P99 −20%。保留 #931 NoGo、#732 M0 NoGo/M1 Hold、#933 feature performance open 和完整 #775 未完成边界。
- 不合并、不发布 release、不打 tag/NuGet；这些不是更新交接资料所包含的动作。

---

## 历史快照（11:54 保存边界、11:50 产品观察；下文待办不代表当前状态）

# 独立交接更新：保存边界核实至11:54；产品观察冻结于11:50 UTC

## 用户明确的保存边界

用户在 #955 合入后询问为何合并；在“提交撤回PR，从dev移出、保留交接分支”的具体提议后明确回复“移除吧”。因此后续交接/证据更新仅进入 `codex/3-release-handoff`，**不再创建交接PR到dev，不借持续发布目标推定允许合并交接资料**。产品、测试和受审工具开发仍按各自授权继续。

原交接ref曾实际缺失，未推定原因。已先恢复并核对 `codex/3-release-handoff=7524fa28e3860c540912c8be76767b575fabb378`，原证据固定输入为 `5a6cf6c490b43c083fb772e2c6f2663fa94bbe16`；11:54读到证据分支已按追加归档推进为`4fa383e71abfef508e2d2deacf7ec7daa6d7ba4b`，原10归档不变。原固定ba99和各失败记录继续保留。

[撤回PR #957](https://github.com/SunSi12138/SharpLink/pull/957) 从dev43cbc创建普通forward revert，只反向删除 #955 增加的18个文档/证据文件；tree `aaf9ce20097cee651d2af9dd9f29f32846ba71ec` 与pre95580965相同，src/test/.github/eng均无变化。已分别独立审核本地c46e与远端d5f185精确HEAD。[PR Fast40682750725](https://github.com/SunSi12138/SharpLink/actions/runs/40682750725)和[CodeQL40682750738](https://github.com/SunSi12138/SharpLink/actions/runs/40682750738)均已success，Fast/CodeQL的6项检查通过；[ready触发的Extended40683408187](https://github.com/SunSi12138/SharpLink/actions/runs/40683408187)于11:53:16UTC启动，在本页11:54截止仍pending，不属于上述6项；release owner于11:53:19UTC以仓库允许的squash合并为`a2e2fd91473668317fde37ba238b8a7c0ec8728c`。独立readback确认PR closed/merged、dev完整tree仍为aaf9ce，和pre95580965零diff，**已从dev移除交接文件**。没有历史重写，交接/证据ref仍live。

之前e7b后续文档候选没有创建远端commit/tree/ref/PR，仅上传了14个不可变blob；不会把其dev-canonical入口原样发布。此次候选改为原交接7524的后继，恢复正确独立分支入口。它本身仍需独立精确HEAD审核后才更新交接ref。

以下产品观察仍固定于11:50UTC；后续raw审核/新计划须读owner最新输出，本页不把稍后事件反复加入同一受审候选。

## #931：原NoGo保留，hosted R1收集完成但不得当性能接受

本地attempt003完整四目标gate失败（两个目标缺优化Tier1）、此前准备失败及部分body/frame增长全部保留，见[本地失败报告](reviews/931-r1-cloud-native/RESULT.md)。没有原样重试本地失败native样本。

工具760a6d7与远端9c0cf9e分别获独立execution-only PASS。一次性打开[Draft验证PR #956](https://github.com/SunSi12138/SharpLink/pull/956)，base为固定工具分支 `codex/931-client-ablation-tools`，**DO NOT MERGE**。同计划的[hosted native run40681832353](https://github.com/SunSi12138/SharpLink/actions/runs/40681832353)已success，PR Fast40681832250亦success；本页仅记录真实收集状态，原始四目标、身份和mechanism独立审核结论尚未在此截止取得，不借绿灯宣布Go。

原全产品 #931 NoGo仍有效；没有新的性能计时、阈值、等价结论或产品合并授权。下一步先读取独立raw审核，而非追加样本。

## #732：收集器IPC失败保留，替代工具尚待独立审核

b1b6工具已独立PASS。attempt001的PhaseProbe和TraceDecoder Release build通过，随后外部dotnet-trace在Unix diagnostic IPC socket处报Permission denied，exit2，空child stdout、没有phase/RPC或trace结果。证据保存在owner的 `artifacts/attempts/phase-001`；没有原样重试或权限提升。

root授权检查运行时支持的direct-file startupEventPipe替代。工具候选 `de51bd5c95ef31846f9681880974d60e6a8a737d` 正在独立审核，11synthetic fixtures通过，此截止尚未新执行。固定两进程/88000调用、providers/caps和失败保留不变；kernel scheduling仍不可用。不能把尚未取得的trace当JIT归因；M0 NoGo/M1 Hold以及F2前提未满足的结论不变。

## #933 与原发布门禁

沿用已独立核查的[完整当前头审计](reviews/933-canonical-40644131003/AUDIT.md)：全部35jobs=27success/8optional browser skips，Unit2439/Integration557等库存已核查；当前10.0.13 primary gap与对称companion分开记录。原25/5只读第一页的局限已纠正并保留说明。

当前correctness/native收齐不解除 #931 前置或 #933 三臂feature performance。有限implementation-only maintenance不等于完整#775。完整发布门禁、API台账、平台、性能、soak和最终main重验继续开放；没有tag/NuGet/Release操作。后续新结果留给下一独立交接checkpoint，不反复改变正在跑门禁的产品/工具PR。
