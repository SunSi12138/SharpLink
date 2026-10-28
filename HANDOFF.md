# SharpLink 3.0 继续执行入口

**用户指定的保存边界：交接资料仅保留在独立交接分支，不再并入 dev。** #955 的18文件已由 [PR #957](https://github.com/SunSi12138/SharpLink/pull/957) 撤回，当时完整 tree 恢复为 pre95580965；当前 dev 已推进到 `e3808b65e694bf1ed250d55fa2ad48a6ef88d285`，2026-10-28 14:06 UTC 再核实交接路径仍不存在。原交接分支与原始证据均保留。当前执行状态见 [独立交接更新](doc/3.0-release-handoff/independent-handoff-status.md)。

目标仍是正式发布 3.0。当前尚未达到发布条件；不要因为交接、订阅到期或 CI 绿色而提前合并性能未验收的候选。

**正确性原则：**行为正确优先，不要求逐项复刻历史偶然执行顺序或 bug；激进优化仍须保证真实公开功能、资源生命周期、取消、错误与协议正确性，真实功能取舍需讨论。3.x 是破坏性升级，允许 API/ABI/wire 变化，不要求 2.x↔3.x 互通；#962 不采纳，已关闭且未合并；跨版本互通不再是发布阻断。仍须保证 3.x 自身正确性、完整功能及迁移说明，官方 2.0.3 仍是性能基线，breaking 不代表允许功能缩水。此当前范围覆盖旧手册/快照中的跨 major 互通要求；3.x 自身 API/包一致性、Trim/AOT、合法与非法协议输入、取消、资源与安全门禁仍有效。

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

## 当前执行顺序（2026-10-28 14:24 UTC）

1. **先读 [最新独立交接状态](doc/3.0-release-handoff/independent-handoff-status.md)。** #961 当前 HEAD `1a76e1b858b9b306f8d09ed6f85bc3c5319e2da5`；nonce 工具与 hosted40704347751 原始证据已独立 PASS，下一步是新源码的 Extended 验证。旧 Extended 的真实 Broken pipe 失败保留，不重跑刷绿。#962 的已通过独立审核/CI 及历史失败证据全部保留，但按用户最新 3.x breaking 决定不采纳，已于 14:18:45 UTC 关闭且未合并，不再等待合并确认，也不作为发布 blocker。#961 狭窄诊断通过不替代完整新源码 Extended。
2. **保留性能 NoGo，不把证据存档当接受。** R1 全产品 run40687272444 已独立确认 dynamic1 P99 六对不利；11 parts / 611 members 已验证并保存在证据提交 `f55d291f5fa1ee4c5807e4b448718e60405fd960`，前 12 个归档不变。#931 NoGo、#732 M0 NoGo/M1 Hold、#933 feature performance open 不变。
3. **以实际性能验证状态机重写。** 阶段化 enumerator607 正确性 PASS，机制结果混合，4 scalar gates PASS / 4 native completeness gates FAIL，尚无 QPS/P99 数据。用户明确代码变大不是无收益结论；此前独立 STOP 建议原样保留为历史意见，当前决定已覆盖，正在准备固定配对端到端性能诊断，尚未执行。新的 terminal-notification 仅为设计；wakeup 正确性与本地 receipt 独立 PASS，但只是 Debian13 prepared-only、零 probe，不能充作冻结 Ubuntu hosted cell；正在准备 hosted prepare → 审 receipt → 同一二进制 collect 方案；borrowed singleton 的 6 pass / 3 fail 保留，不冒充性能主线。
4. **最后才是冻结候选与完整发布验收。** 尚无官方 2.0.3 对比满足 QPS +10% / P99 −20%；完整平台、API、性能、soak 与最终 main SHA 验收未完成。受限 maintenance 不能关闭完整 #775。没有合并、tag、NuGet 或 Release 操作。

[状态总表](doc/3.0-release-handoff/status.json) 与最新页面优先于旧实验附录。旧「运行中 / 待审核 / 未推送」保留为历史，不能当当前事实；先核对真实 HEAD 和已有证据，不重复失败实验。

## 已经完成的工作

本轮目标中已合并 #936、#937、#938、#939、#940、#718、#941、#942、#951、#952、#943、#953、#954；#691 已关闭。当前 dev 为 `e3808b65e694bf1ed250d55fa2ad48a6ef88d285`（后续已含 #958 / #959；#957 撤回交接的历史仍保留，当前 tree 已不是旧 `80965d5`。交接仅保留独立分支，继续查询远端）。

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
