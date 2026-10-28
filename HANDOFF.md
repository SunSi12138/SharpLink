# SharpLink 3.0 继续执行入口

**用户指定的保存边界：交接资料仅保留在独立交接分支，不再并入 dev。** #955 的18文件已由 [PR #957](https://github.com/SunSi12138/SharpLink/pull/957) 撤回，当时完整 tree 恢复为 pre95580965；当前 dev 已推进到 `57f06f172d29fa5cfd4ea8e9b86a23f122df24ab`，2026-10-28 16:15 UTC 再核实交接路径仍不存在。原交接分支与原始证据均保留。当前执行状态见 [独立交接更新](doc/3.0-release-handoff/independent-handoff-status.md)。

目标仍是正式发布 3.0。当前尚未达到发布条件；不要因为交接、订阅到期或 CI 绿色而提前合并性能未验收的候选。

**正确性原则：**行为正确优先，不要求逐项复刻历史偶然执行顺序或 bug；激进优化仍须保证真实公开功能、资源生命周期、取消、错误与协议正确性，真实功能取舍需讨论。3.x 是破坏性升级，允许 API/ABI/wire 变化，不要求 2.x↔3.x 互通；#962 不采纳，已关闭且未合并；跨版本互通不再是发布阻断。仍须保证 3.x 自身正确性、完整功能及迁移说明，官方 2.0.3 仍是性能基线，breaking 不代表允许功能缩水。此当前范围覆盖旧手册/快照中的跨 major 互通要求；3.x 自身 API/包一致性、Trim/AOT、合法与非法协议输入、取消、资源与安全门禁仍有效。

接手者先读本文件，再读 [当前任务与证据](doc/3.0-release-handoff/status.json)、[发布门禁与合并步骤](doc/3.0-release-handoff/release-gates-and-merge.md) 和 [技术方案决策树](doc/3.0-release-handoff/technical-decision-tree.md)。后两份文件给出具体命令、结果分支、实现位置及必须保留的行为。

## 云端接手，先恢复事实

**云端入口：**[本文件](https://github.com/SunSi12138/SharpLink/blob/codex/3-release-handoff/HANDOFF.md)。先读 [云端资源与恢复命令](doc/3.0-release-handoff/cloud-resources.md)。已归档的代码与证据通过 GitHub 恢复；尚未归档的本地原件由最新检查点明确标注，不能把本地路径当下载链接。后面的 `/Users/sunsi/...` 仅说明历史运行位置，云端不需要这些目录。

如果环境已有该仓库，先检查现有修改并复用；没有仓库时：

```sh
git clone --filter=blob:none --branch codex/3-release-handoff https://github.com/SunSi12138/SharpLink.git SharpLink
cd SharpLink
git status --short --branch
git fetch origin
# 有 GitHub CLI 时查询实际远端状态；公开文档也可直接通过网页/API读取。
gh pr list --repo SunSi12138/SharpLink --state open --limit 30
```

以 `status.json` 的远端分支、提交和运行编号定位。旧日志的“运行中”不证明任务仍活跃；查询真实 Actions 状态，已完成的运行直接下载结果。观察超时不等于任务失败，不因旧代理消失而重跑已完成实验。

不要破坏环境中已有修改，或重复未改变的失败实验。先读实际 diff、已有日志及最后提交。同一源码、同一输入的有效证据直接复用。

## 当前执行顺序（2026-10-28 16:15 UTC）

1. **#961 已合并，不再待审。** 用户明确同意后，15:07:19 UTC squash 到 dev `57f06f17`，tree 与受审 `1a76e1b8` 相同。适用 CI、新 Extended 的 Integration491/491 及原生 codec2088 rows 均独立通过；旧 Broken pipe 失败、缺失的首个本地 native 报告与 optional browser skips 仍保留。
2. **607 已完成真实性能诊断，目前不晋级、不合并。** #963 hosted40712925050 与 local76b8de5 分别独立复核，不能混样本。Hosted 四 cell QPS −0.01/−0.35/−1.42/−0.51%，所有 QPS/CPU/P99 区间跨无变化；static1 P99.9 +24.28% 是未多重校正的描述性风险。结论来自真实测量，不是代码变大。完整 30-part 证据已保存到 `f90e9adf`，之前 13 个归档不变。
3. **继续两个尚未形成性能结论的方向。** 新 logical-forwarder 基于 ac17、不叠加607，源码/针对性正确性与4进程机制独立 PASS；off/direct/pending 分配斜率约136→0，但 AsTask约88、Telemetry on约272两臂无改善、sync约+8B/call，真实 TCP 尚未测。Wakeup Linux hosted preparation/receipts PASS，ARM三个build成功但原receipt FAILED；ARM-only纯字节revalidate工具正在审核，零 collect。只读 profiling 证据查找尚未产出新 profile。
4. **完整发布门禁仍开放。** #962 closed/unmerged 且不采用跨 major 互通要求；#931 NoGo、#732 M0 NoGo/M1 Hold、#933 feature performance open、完整#775未交付不变。尚无官方2.0.3对比达到QPS+10%/P99−20%；未操作main/tag/NuGet/Release。具体来源、失败边界与下一步见 [最新检查点](doc/3.0-release-handoff/independent-handoff-status.md)。

[状态总表](doc/3.0-release-handoff/status.json) 与最新页面优先于旧实验附录。旧「运行中 / 待审核 / 未推送」保留为历史，不能当当前事实；先核对真实 HEAD 和已有证据，不重复失败实验。

## 已经完成的工作

本轮目标中已合并 #936、#937、#938、#939、#940、#718、#941、#942、#951、#952、#943、#953、#954；#691 已关闭。当前 dev 为 `57f06f172d29fa5cfd4ea8e9b86a23f122df24ab`（后续已含 #958 / #959 / #961；#957 撤回交接的历史仍保留，当前 tree 已不是旧 `80965d5`。交接仅保留独立分支，继续查询远端）。

API inventory 和 302 类型 / 3397 decision IDs 的分类台账已合入。台账仍保留固定来源及 `proposal-not-approved` 发布状态：最终 #732/#931/#933 的声明差异、真实九包对比和冻结 HEAD 验收还要完成，#805 继续 open。

## 不能丢失的证据和约束

- 关键证据已另存 [GitHub 证据分支](https://github.com/SunSi12138/SharpLink/tree/codex/3-release-evidence)，下载方法及内容索引见云端资源页。Actions artifacts 作为另一份来源，通常有保留期限；证据分支归档没有 Actions 的自动过期时间。不要只保存摘要而删除失败、raw ticks 或二进制来源。
- #931 原 48-process 配对结果有重复不利观察；#732 原 M0 结果为 NoGo，后来本地 M0/M1 受外部构建干扰而 inconclusive。新的有利结果不能悄悄覆盖旧数据。
- 不增加性能豁免、不删异常样本、不重试刷绿、不把不显著当等效。没有满足预声明验收条件时，保持 Draft，选择决策树中的下一项有限实验；需要重大产品权衡时向用户提出具体选择。
- 每个 PR 必须由未参与其实现的代理独立审核通过，之后才能合并。平台配置没有强制要求批准人数，不能依赖 GitHub 自动替你落实这一条。
- 不把管理查询、字典、epoch/hash 校验或新计数器加入普通 RPC 热路径。冷发布修复也必须保持用户 metadata SPI 的原有锁条件和重入约束。
- 最终发布前不得打 `v*` 标签试跑。标签会触发真实 NuGet 发布。

## 云端运行约定与原本机备注

仓库 `global.json` 决定 .NET SDK 和 native MTP 模式；TUnit 定向过滤使用 `--treenode-filter`，不是 VSTest `--filter`。执行 .NET 测试前使用已安装的 `dotnet-test:run-tests` skill，并核对实际执行数量。云端使用满足脚本要求的 Python（建议 3.12+）；`/opt/homebrew/bin/python3` 只是原 Mac 的解释器路径，不是云端前置条件。

以下通知脚本仅属于原 Mac，云端不存在时不阻断工作、不伪称已通知。用户原本要求：真正需要用户输入时，先调用 `/usr/bin/python3 /Users/sunsi/.codex/qq-notify/need-input.py "简短提示"`，并按其通知规则同时提醒小爱与 QQ；完成用户交付的实际任务时，在最终答复前调用 `/usr/bin/python3 /Users/sunsi/.codex/xiaoi/speak.py "简短结果"`。普通进度更新无需播报。通知不得包含敏感信息；失败不阻止交付。
