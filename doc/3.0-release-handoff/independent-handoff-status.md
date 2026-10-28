# 独立交接检查点：2026-10-28 16:15 UTC

**当前决定：#961 已获用户明确同意并合入 dev；607 已完成真实性能测试但不晋级、不合并；logical-forwarder 有狭窄正向机制证据，真实 TCP 性能尚未测；wakeup 仍只完成部分受审准备，没有 collect。尚无官方 2.0.3 对比达到 QPS +10% / P99 −20% 的验收结果。**

本检查点固定于 16:15 UTC，后续并发事件留给下一次更新。[只读 GitHub 快照](reviews/checkpoint-20261028-1615/github-observations.json) 核实 dev=`57f06f172d29fa5cfd4ea8e9b86a23f122df24ab`，tree=`e5b72625377ffdcf5f5470e2ddd69be12a449d8e`；完整 recursive tree 未截断，交接路径仍不存在。此次从独立交接父提交 `d0f493fbf25bb3b4f90f758dc5b098241ac1f0e6` 更新，不把文档并入 dev，不创建交接 PR。

用户已允许性能验证 PR；这不把验证 PR 变成产品合并许可。3.x breaking 范围保持：允许 API/ABI/wire 变化，不要求 2.x↔3.x 互通；[#962](https://github.com/SunSi12138/SharpLink/pull/962) 仍 closed/unmerged、不采纳、非 release blocker。3.x 自身正确性、完整功能、包/生成器一致性、Trim/AOT、协议合法/非法输入、取消、资源、安全及迁移说明仍须成立；breaking 不是功能缩水许可。官方 2.0.3 仍为性能基线。

以下引用的正式报告是当时的独立技术记录；原件保留，除明确标注省略内部协作信息的技术摘录外，副本均未改写。其中旧的“尚待合并确认”“后续尚未测量”等时态只代表报告时点，当前状态以本节为准。历史 failed、skipped、缺失报告及不利样本均不改判。

本次只增加报告/技术摘录，不把它们称为完整 raw 镜像。Phase 完整 raw 已在 f90e9adf 持久归档；#961/wakeup 的原始 Actions 工件仍有保留期限，logical-forwarder 的完整本地 corpus 尚未在本检查点声称上传。报告内的本地路径、verify.py/JSON 文件名是原来源位置；仅本目录实际列出的报告副本可从本交接下载。

## #961：适用技术门禁 PASS，已明确授权并合并

用户于 15:05 UTC 明确同意合并；[PR #961](https://github.com/SunSi12138/SharpLink/pull/961) 于 **15:07:19 UTC** squash 为 [`57f06f172d29fa5cfd4ea8e9b86a23f122df24ab`](https://github.com/SunSi12138/SharpLink/commit/57f06f172d29fa5cfd4ea8e9b86a23f122df24ab)。完整 tree 与受审 PR HEAD `1a76e1b858b9b306f8d09ed6f85bc3c5319e2da5` / 本地 `555cdb9f8e7230b555257ddc86ce8fcb4dc0181d` 相同，不再列为待审/待合并。

[最终独立技术报告](reviews/checkpoint-20261028-1615/pr961-final-review.md)：全部适用 CI terminal success；新 [Extended40706353475](https://github.com/SunSi12138/SharpLink/actions/runs/40706353475) 24 jobs = 16 success / 8 optional browser skips，完整无过滤 Integration **491/491，0 failed/skip**。六原生 codec consumer 的 raw 报告与汇总逐行一致：**2088 rows = 36 ordered pairs × 58 fixtures**，零 blocking failures。不能把 optional skips 算成通过。

重要证据边界：Extended 没有上传 Integration 全部 per-case native JSON/HTML；491/491 来自原始 runner summary，目标测试包含性由精确源码、无过滤命令与零 skip 支持，另有同树 hosted diagnostic 的具名 native JSON 通过证明。不得声称检查了并不存在的完整逐例报告。旧 Extended40697027111 的真实 490/491 Broken pipe 仍为失败；首个本地 native JSON/HTML 在后续 attempt 覆盖前未保存，仅 stdout/stack/build 留存，不能重建成“已留存完整报告”。后续本地 Permission denied 与 hosted 受控负例也分别保留。已合并的是正确性修复，不是性能增益或完整 3.0 发布接受。

## 607 阶段化重写：已实测，不晋级；不是按代码大小判定

[#963](https://github.com/SunSi12138/SharpLink/pull/963) 为 Draft 验证 PR，精确工具 `90d98e3cb84ce36425b99c88c8112632194834b5`，**DO NOT MERGE**。对照为原 `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c` 与实际构建候选 `2153cd9228c1de25e89a4bb68f0e3da4dc19f867`（tree `02a2824f344c3614fff8de1d8a10ed7a5be87c21`，与受审原 607 whole tree 相同），不是官方 2.0.3。

- [本地独立报告](reviews/checkpoint-20261028-1615/phase-local-review.md)：工具 `76b8de5fc52e0a32ed8bc7a2e3772e9c1b29e6fb`，48 TCP processes / 24 pairs、14 scalar processes，4,137,425 ticks，604 文件 receipt 完整。四 cell QPS 依次 −1.80%、−1.61%、+3.14%、+1.03%；QPS/CPU/latency 的描述性 95% 区间均跨无变化。观察到 guest steal 非零：1.474%–6.854%，median 3.483%，覆盖进程边界而非仅计时窗。结果 mixed/inconclusive，不校正/删去这些样本。
- [Hosted40712925050](https://github.com/SunSi12138/SharpLink/actions/runs/40712925050) 与 [独立报告](reviews/checkpoint-20261028-1615/phase-hosted-review.md)：48 TCP processes / 24 pairs、14 scalar processes，3,551,642 ticks，596 个原始 ZIP members 全部逐字节核验。每 cell 六个进程对，统计单位不是单次 RPC。

| Hosted cell | QPS paired-mean | CPU/RPC | P99 | P99.9 |
|---|---:|---:|---:|---:|
| static1 | −0.01% | −0.33% | −1.44% | +24.28% |
| static100 | −0.35% | +0.55% | +2.95% | −5.94% |
| dynamic1 | −1.42% | +0.47% | −3.04% | −11.23% |
| dynamic100 | −0.51% | +0.50% | +3.24% | +7.35% |

所有 QPS/CPU/P99 描述性 95% 区间跨 0。static1 P99.9 +24.28%，区间 +2.80%..+52.72%，但六对是 **3 不利 / 3 有利**，不是六对同向；它是未做多重比较校正的描述性尾延迟风险，不是正式回退证明。不要混淆 paired mean 与 ratio-of-medians。两臂 scalar 分配在全部 12 cells / 五个 FullOpts pairs 完全相同。

Hosted 选定 CPU0/CPU1 同属 guest CORE0，是 SMT siblings；steal=0 不证明物理隔离或无争用。本地与 hosted 拓扑/争用不同，**不得合并样本**。当前不晋级、不合并 607，是因为这两个有界真实测试未建立吞吐/CPU 收益且保留尾延迟风险，不是因为代码变大。旧机制完整性失败、旧 STOP 建议原样作为历史保留；不能继续把“尚无 QPS/P99”当当前事实，也不增加样本刷绿。

### 持久证据已保存，不依赖 Actions 留存期

证据分支 [`f90e9adf565cb3e96adbaa9d150d7f03712a9a80`](https://github.com/SunSi12138/SharpLink/tree/f90e9adf565cb3e96adbaa9d150d7f03712a9a80) 已公开保存完整 phase 实验、旧机制失败、准备失败及独立复算。[发布核验报告](reviews/checkpoint-20261028-1615/phase-publication-review.md)、[固定 README](https://github.com/SunSi12138/SharpLink/blob/f90e9adf565cb3e96adbaa9d150d7f03712a9a80/README.md)、[30-part manifest](https://github.com/SunSi12138/SharpLink/blob/f90e9adf565cb3e96adbaa9d150d7f03712a9a80/archives/phase-rewrite-performance-20261028.parts/manifest.json)、[5072-member inventory](https://github.com/SunSi12138/SharpLink/blob/f90e9adf565cb3e96adbaa9d150d7f03712a9a80/phase-rewrite-performance-20261028-files.json)。

重组 **93,087,850 bytes / 5,072 members / 30 parts**，SHA-256 `8c1cd1ff0f165eba1d0912d5fbe75604030c8ce895cf359f15b38b260dab6454`；前 13 个逻辑归档及其 24 个存储路径未变。当前 **14 个逻辑归档，总 229,505,339 bytes**。按 manifest 顺序验证每片及整包后才解压；不是仓库已存有单体 tar.gz。首次上传截断形成的孤立错误 blob 未进入任何 tree/ref，相关失败留存；成功发布只是证据完整性，不是性能 Go。

## Logical-forwarder：实现与机制 PASS，真实 TCP 未测

这是新轴线：本地 `d17e1ac691325b8948ca8663ba2d2b836c3ac2a0` 基于原 ac17，**没有叠加 607**；远端等价源 [`484a5d279a84a3cd9e717c1ec94d3a5a415112df`](https://github.com/SunSi12138/SharpLink/commit/484a5d279a84a3cd9e717c1ec94d3a5a415112df)，tree `5c0060b2fbd7ca41deb78022170148992d32eea6`。它移除真正的 pending logical forwarding layer，不是只把 driver 代码搬进 helper。

[独立实现审核](reviews/checkpoint-20261028-1615/logical-implementation-review.md) PASS 限于源码、设计落实与已有针对性正确性：candidate/baseline dedicated 各 22、inner 各 17、candidate adjacent 109、publication overlay 2，均通过；不冒充全量 release suite。早期 14/20 项实际进入 Telemetry fallback，不能算优化路径验收；最终加入实际 wrapper 身份断言。

[独立机制审核](reviews/checkpoint-20261028-1615/logical-mechanism-review.md)：工具 `01f6aad58dd14278497a219dfad4f96906b8919a`，4 个非计时进程，每进程全部 24 cells，189/189 census 文件完整，原始 backing/continuation 图支持 direct pending 路径确实移除了逻辑转发 box。观察到：
- Telemetry off / direct / pending：long slope 约 **136 → 0 B/item**；
- off / AsTask / pending：候选仍约 **88 B/item**，不能把 direct 结果推广给转换桥；
- Telemetry on / pending：两臂约 **272 B/item**，未证明 fallback 改善；
- 同步路径约 **+8 B/call**，不利结果保留。

这是每臂一次 allocation process 的进程级总量，包含 harness/runtime/scheduling；实际 allocation 窗口的 forwarding suspension 次数 W 未测，不能把 item count 当 W，也不是 allocation-stack 归因、精确 GC 责任或 QPS/P99 收益。后续重型 scanner 的已知时间在本机制结束之后，不据此宣称污染；完整静默窗口也未被穷尽证明。真实 TCP 工具正在准备，尚未测量；应冻结实际路径、Telemetry 与 AsTask 范围后做独立受审端到端测试，不与 607 混样本。

## Wakeup：Linux 准备通过；ARM 原 receipt 失败保留，尚未 collect

产品远端 C `e5a35b9bf63d162c3077aec389d402e28bd2fb53` 等价受审 c17；没有改变正确性 PASS 的边界。首个 prepare [40710903027](https://github.com/SunSi12138/SharpLink/actions/runs/40710903027) 因环境 guard 失败，原样保留。

第二 prepare [40716132624](https://github.com/SunSi12138/SharpLink/actions/runs/40716132624) 的整体 workflow **failure**；[独立拆分裁决](reviews/checkpoint-20261028-1615/wakeup-hosted-preparation-review.md) 是：
- **Linux x64 PASS（仅准备）**：artifact12828492592，bundle/两份 D/C receipts 已独立核验，1741 regular members、每臂1091 bound files；Ubuntu24.04，SDK10.0.102/runtime10.0.14，三个 Release builds 零 warning/error。保留同一 bundle/receipt，不重建、不替换 Linux bytes。
- **ARM64 原 receipt 仍 FAILED，不可 collect**：artifact12828441906，实际 macOS14.8/arm64，三个 builds 均成功；13 个 Core/ASP 共用 dylib 路径由既定 Core→ASP 提取顺序覆盖，旧验证器错误地逐 component 比对。独立完整检查确认最终 336 runtime files 全部符合固定 last-writer 官方 archive bytes；不声称冲突只是签名差别，不重签、不归一化、不改原失败。

候选 revalidation 工具 `46bb5bf37eb0da3ae225cdce4f0af98049e067ce` 正在独立审核：只对原 ARM 工件做纯字节恢复/重验，**不 build、不运行 CLR/probe/decoder、不重签名、不改 Linux**，新的 receipts 须使用新身份并保留原 receipt。这不是新的准备运行或已执行的 collect。现有协议仍要求两架构 receipts 批准后才能 collect；截至本快照零 collect、零 native 性能结果，不能凭 Linux PASS 自动开始单架构运行。早先 Debian13 本地 receipt PASS 仍仅 prepared-only，不替代 hosted cell。

## 下一步与仍开放的验收

1. 607 本轮不晋级、不合并、不补有利样本；维护 #963 的验证用途与持久证据即可。此前代码大小 STOP 建议由真实测试裁决取代，不删除任何原始意见/失败。
2. Logical-forwarder 准备真实 TCP 工具及冻结配对方案，独立审核后执行；报告 off/direct 的实际覆盖、AsTask/Telemetry fallback、同步开销及资源/错误行为，不仅挑有利 allocation slope。
3. 完成 wakeup ARM-only 字节 revalidation 工具审核，再对原 bytes 产生新的可审核 receipts；Linux 保持原 bytes。两架构 gate 未满足前不 collect。
4. 只读寻找实际 CPU/allocation-stack 证据的工作刚开始；**尚未形成新的 profile**，不得把假设或已有 allocation 总量称为已取得堆栈归因。
5. #931 NoGo、#732 M0 NoGo/M1 Hold、#933 feature performance open、完整 #775 未交付均不变。最终官方 2.0.3 的产品性能比较、平台/soak/API 与完整功能验收仍开放。没有 dev→main、tag、NuGet 或正式 Release 操作。

---

## 历史检查点（14:24 UTC；下文旧待办不代表当前状态）

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
