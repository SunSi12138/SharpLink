# SharpLink 3.0 继续执行入口

目标仍是正式发布 3.0。当前尚未达到发布条件；不要因为交接、订阅到期或 CI 绿色而提前合并性能未验收的候选。

接手者先读本文件，再读 [当前任务与证据](doc/3.0-release-handoff/status.json)、[发布门禁与合并步骤](doc/3.0-release-handoff/release-gates-and-merge.md) 和 [技术方案决策树](doc/3.0-release-handoff/technical-decision-tree.md)。后两份文件给出具体命令、结果分支、实现位置及必须保留的行为。

## 先恢复事实，不重新开始

仓库：`/Users/sunsi/Documents/ChatGPT/SharpLink`。此处的 `dev` 是已合并代码；实验及修复各有独立工作树。先执行只读检查：

```sh
cd /Users/sunsi/Documents/ChatGPT/SharpLink
git status --short --branch
git fetch origin dev main
git worktree list
gh pr list --repo SunSi12138/SharpLink --state open --limit 30
```

以 `status.json` 中的提交、工作树、运行编号定位工作，再向 GitHub 查询这些具体运行。日志文件或旧代理的“正在运行”文字不证明进程仍活跃；确认真实 PID、exec session 或 Actions 状态后再等待或继续。观察超时不等于任务结束。失效的代理不承担后台工作，需由当前助手接管其已保存成果。

不要 `reset --hard`、清理其它工作树、终止其它项目的进程，或重新运行未改变的失败实验。先读对应工作树的 `git status`、已有日志及最后提交。局部检查已有同一源码的有效结果时直接复用。

## 当前执行顺序

1. **收取并解释 #732 三臂 CI 和 #931 Client 对照 CI。** 精确工具版本、运行编号和结果规则在 `status.json`。确认准备门禁、完整样本、真实加载程序集及原始失败，再解释性能；不是只看 workflow success。
2. **收取 #933 修复后的完整 CI。** 修复已独立审核，本地 Unit 2439 / Integration 557 通过；hosted 的 native companion 和完整发布门禁仍需实际结果。失败按手册进入具体修复分支。
3. **按对照结果选择下一项产品修改。** #931 已有最小 loop-exit 隔离实验；#732 有完整 facts-once 的 M0/M1 和条件化备选。先读技术决策树，保留原所有权、异常、CTS、ALC 与公开 SPI 条件，不凭直觉重写。
4. **独立审核最终候选，再按依赖合入 dev。** 产品代码、测试和证据必须对应正在合并的 SHA。#931 与 #933 当前仍是 Draft；原 #775 完整 Binding/receipt 范围仍未完成，不能用受限 maintenance 能力关闭它。
5. **最后冻结 release candidate，完成完整门禁，再 dev → main、重验 main SHA、tag/NuGet。** 完整性能矩阵远大于 smoke，必须按分片计划执行并汇总。不能把一次诊断、一个平台或零分配结果当作完整发布验收。

## 已经完成的工作

本轮目标中已合并 #936、#937、#938、#939、#940、#718、#941、#942、#951、#952、#943、#953；#691 已关闭。当前已合并基线为 `eec9524aa21251a1d548f33e363dd4c51094cb14`。

API inventory 和 302 类型 / 3397 decision IDs 的分类台账已合入。台账仍保留固定来源及 `proposal-not-approved` 发布状态：最终 #732/#931/#933 的声明差异、真实九包对比和冻结 HEAD 验收还要完成，#805 继续 open。

## 不能丢失的证据和约束

- 本地证据集中在 `artifacts/release-readiness/2026-10-28/`，其它证据路径列于 `status.json`。它们多数被 Git 忽略，**归档或删除工作树前必须先保留这些原始证据**。不要只保存摘要而删除失败、raw ticks 或二进制来源。
- #931 原 48-process 配对结果有重复不利观察；#732 原 M0 结果为 NoGo，后来本地 M0/M1 受外部构建干扰而 inconclusive。新的有利结果不能悄悄覆盖旧数据。
- 不增加性能豁免、不删异常样本、不重试刷绿、不把不显著当等效。没有满足预声明验收条件时，保持 Draft，选择决策树中的下一项有限实验；需要重大产品权衡时向用户提出具体选择。
- 每个 PR 必须由未参与其实现的代理独立审核通过，之后才能合并。平台配置没有强制要求批准人数，不能依赖 GitHub 自动替你落实这一条。
- 不把管理查询、字典、epoch/hash 校验或新计数器加入普通 RPC 热路径。冷发布修复也必须保持用户 metadata SPI 的原有锁条件和重入约束。
- 最终发布前不得打 `v*` 标签试跑。标签会触发真实 NuGet 发布。

## 本机运行约定

仓库 `global.json` 决定 .NET SDK 和 native MTP 模式；TUnit 定向过滤使用 `--treenode-filter`，不是 VSTest `--filter`。执行 .NET 测试前使用已安装的 `dotnet-test:run-tests` skill，并核对实际执行数量。需要新版本 `tarfile` 功能的 Python 脚本使用 `/opt/homebrew/bin/python3`；不要修改系统 Python。

用户还要求：真正需要用户输入时，先调用 `/usr/bin/python3 /Users/sunsi/.codex/qq-notify/need-input.py "简短提示"`，并按其通知规则同时提醒小爱与 QQ；完成用户交付的实际任务时，在最终答复前调用 `/usr/bin/python3 /Users/sunsi/.codex/xiaoi/speak.py "简短结果"`。普通进度更新无需播报。通知不得包含敏感信息；失败不阻止交付。
