# SharpLink 3.0 继续执行入口

**用户指定的保存边界：交接资料仅保留在独立交接分支，不再并入 dev。** #955 的18文件已由 [PR #957](https://github.com/SunSi12138/SharpLink/pull/957) 撤回，dev=`a2e2fd91473668317fde37ba238b8a7c0ec8728c`，完整tree恢复为pre95580965；原交接分支与原始证据均保留。当前执行状态见 [独立交接更新](doc/3.0-release-handoff/independent-handoff-status.md)。

目标仍是正式发布 3.0。当前尚未达到发布条件；不要因为交接、订阅到期或 CI 绿色而提前合并性能未验收的候选。

接手者先读本文件，再读 [当前任务与证据](doc/3.0-release-handoff/status.json)、[发布门禁与合并步骤](doc/3.0-release-handoff/release-gates-and-merge.md) 和 [技术方案决策树](doc/3.0-release-handoff/technical-decision-tree.md)。后两份文件给出具体命令、结果分支、实现位置及必须保留的行为。

## 云端接手，先恢复事实

**云端入口：**[本文件](https://github.com/SunSi12138/SharpLink/blob/codex/3-release-handoff/HANDOFF.md)。先读 [云端资源与恢复命令](doc/3.0-release-handoff/cloud-resources.md)。代码分支、实验提交和证据均在 GitHub；后面的 `/Users/sunsi/...` 仅说明历史运行位置，云端不需要这些目录。

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

## 当前执行顺序

1. **#931 默认 PGO 对照 run40646151477 已独立复核：12 进程 / 354648 ticks 完整，Client-only 方向混合、inconclusive；原全产品 NoGo 不变。** 先读 [独立报告与可复算输出](doc/3.0-release-handoff/reviews/931-pgo-40646151477/INDEPENDENT-REVIEW.md)，再读 #732 三臂与 #931 FullOpts 结果。实际 TCP Tier1 的重复 `hasNext` 支持下一项 **R1-only、有界非计时机制比较**，不支持直接合并或重复旧计时实验。本地 R1 非计时尝试已完成，但缺少 Runtime/Server 两个目标的优化 Tier1，完整门禁 Failed；不得用部分重复分支消失宣称接受。随后已审核的同计划工具在 hosted run40681832353 收集成功；原始四目标证据的独立裁决仍须单独确认，不由 workflow success 宣布性能接受。
2. **#933 当前头 `10b1bf03` 的 canonical run40644131003 已核实 success。** fast/extended、X64/Arm64 allocation、10.0.2/10.0.13 producer/native 与 enabled-TLS jobs 均成功；[原始证据已独立审计](doc/3.0-release-handoff/reviews/933-canonical-40644131003/AUDIT.md)：Unit2439/Integration557、对称 companion 完整，但不是全293文件的新源码批准或性能接受。#931 前置和 #933 feature performance 仍阻断合并；最终发布门禁仍未完成。
3. **按对照结果选择下一项产品修改。** #931 已有最小 loop-exit 隔离实验；#732 有完整 facts-once 的 M0/M1 和条件化备选。先读技术决策树，保留原所有权、异常、CTS、ALC 与公开 SPI 条件，不凭直觉重写。
4. **独立审核最终候选，再按依赖合入 dev。** 产品代码、测试和证据必须对应正在合并的 SHA。#931 与 #933 当前仍是 Draft；原 #775 完整 Binding/receipt 范围仍未完成，不能用受限 maintenance 能力关闭它。
5. **最后冻结 release candidate，完成完整门禁，再 dev → main、重验 main SHA、tag/NuGet。** 完整性能矩阵远大于 smoke，必须按分片计划执行并汇总。不能把一次诊断、一个平台或零分配结果当作完整发布验收。

云端接手的工具初检见 [接手进度](doc/3.0-release-handoff/cloud-takeover-status.md)，后续 R1 失败门禁、#933 原始证据审计和 #732 诊断见 [最新进度](doc/3.0-release-handoff/cloud-followup-status.md)。旧实验附录中的“待审核/未推送”叙述是当时记录，以当前总表及精确远端事实为准。

## 已经完成的工作

本轮目标中已合并 #936、#937、#938、#939、#940、#718、#941、#942、#951、#952、#943、#953、#954；#691 已关闭。当前 dev 为 `a2e2fd91473668317fde37ba238b8a7c0ec8728c`（#957 撤回交接文档后，完整tree与含 #954 的产品/工具基线 `80965d5` 相同；交接仅保留独立分支，继续查询远端）。

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
