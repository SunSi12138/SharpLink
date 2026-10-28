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
