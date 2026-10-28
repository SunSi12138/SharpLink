# 云端接手所需的代码与证据

统一入口是 [HANDOFF.md](https://github.com/SunSi12138/SharpLink/blob/codex/3-release-handoff/HANDOFF.md)。不需要原 Mac、原聊天上下文或 `/Users/sunsi` 目录。本文把那些历史位置映射到 GitHub 分支和持久证据。

## 代码已经保存到哪些分支

仓库：`https://github.com/SunSi12138/SharpLink.git`。复用云端已有 checkout；没有时按统一入口 clone。所有修改都应从对应候选开始，不要把实验分支当作已接受的 dev。

| 用途 | 远端分支 | 固定参考提交 |
|---|---|---|
| 当前开发基线，含 #959；交接仍不在 dev | `dev` | `e3808b65e694bf1ed250d55fa2ad48a6ef88d285`（14:06 UTC 核实；不是发布验收） |
| 本交接资料 | `codex/3-release-handoff` 独立持续入口，按用户要求不再并入 dev | 本次更新前远端为 `98ea64cab08190615e7022d5964fae40ec6c5bff`；原固定交接 `ba99c6f9` 不改写；dev中的 #955 已由 PR #957 单独撤回 |
| #931 物理所有权候选，性能 NoGo | `fix/physical-work-ownership-20261022` | `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c` |
| #931 Client-only 工具，FullOpts 和默认 PGO profile | `codex/931-client-ablation-tools` | `51f5bd0ec3f600196f2e26ea48a466ec2cea62d8` |
| #931 loop-exit 隔离实验和报告，尚非合并候选 | `codex/931-loop-exit-experiment` | `87f76fb805c70693513a87c5acec03e9643e910a`；产品改动在 `94adeae8` |
| #732 pre-M 输入 | `codex/732-evidence-pre-m` | `a6bd0352a931d5c2816e42778055e90efb01144b` |
| #732 M0 输入 | `codex/732-evidence-m0` | `37548b7a35d50803212d2b0a637306636cc0a1d3` |
| #732 M1 输入 | `codex/732-evidence-m1` | `2756e8810772cf956ee70ed380925ecc86e86a32`；产品表示改动在 `652ffca7` |
| #732 三臂工具及固定分析方法 | `codex/732-controlled-performance-tools` | `47cc9ec301911c876cc4a1b1d1aa5eb5faa6d33f` |
| #933 已审核修复，性能仍待接受 | `feat/cooperative-maintenance-20261022` | `10b1bf03881337c0801d6ff24318bb479747ca6c` |
| full 矩阵计划/覆盖工具 | `dev`（原审查分支已在合并后删除） | 原 review commit `2422d3b40d9a64380c5f75cb6bcd87a67db9c17f`；实际入口在 dev `80965d5`，审查记录见 [#954](https://github.com/SunSi12138/SharpLink/pull/954) |

例如，在现有 repo 中隔离查看 #933：

```sh
git fetch origin refs/heads/feat/cooperative-maintenance-20261022
git worktree add --detach ../maintenance-933 10b1bf03881337c0801d6ff24318bb479747ca6c
```

先核对远端最新 head。后续若有新提交，比较差异并确认审核/门禁的适用范围；不要硬把上表旧 SHA 当最新生产结果。

## 持久证据不依赖 Actions 过期时间

[证据分支 README](https://github.com/SunSi12138/SharpLink/blob/codex/3-release-evidence/README.md) 和 [机器索引 index.json](https://github.com/SunSi12138/SharpLink/blob/codex/3-release-evidence/index.json) 列出每个归档的来源、内容、排除项及解包方式。归档位于该分支 `archives/`，与 dev 代码分开保存。最新核实证据提交为 `f55d291f5fa1ee4c5807e4b448718e60405fd960`：13 个逻辑归档，共 136,417,489 字节；其中 full-product 归档以 11 个有序 binary parts 保存，单体 tar.gz 仅是本地重组路径。原 `5a6cf6c490b43c083fb772e2c6f2663fa94bbe16` 的 10 个归档及后续失败证据仍保留。恢复须使用固定提交 README 的 fail-closed 校验命令，不直接解压不存在的单体路径。详见 [最新状态与精确 hash](independent-handoff-status.md)。

```sh
git clone --depth 1 --single-branch --branch codex/3-release-evidence \
  https://github.com/SunSi12138/SharpLink.git SharpLink-evidence
cd SharpLink-evidence
cat README.md
cat index.json
```

本地历史实验的原始进程命令行可能涉及其他项目，公有镜像按索引明确剔除或脱敏这些环境细节；timing ticks 和计算输入保持原字节。部分 MSBuild binlog 因可能含环境变量而不公开，对应文本构建日志、源码及程序集来源仍保留。不得把脱敏镜像声称为含所有私有环境字段的原件。

## 已完成的 Actions 运行

| 运行 | 用途 | 当前可用结论 |
|---|---|---|
| [40634900826](https://github.com/SunSi12138/SharpLink/actions/runs/40634900826) | #931 默认 PGO 全产品，48 processes | 独立复核为 NoGo；dynamic100 的 P99 六对全不利 |
| [40642932620](https://github.com/SunSi12138/SharpLink/actions/runs/40642932620) | #931 FullOpts Client-only，12 processes | 完整，未复现原持续不利方向；不能解除原全产品 NoGo |
| [40646151477](https://github.com/SunSi12138/SharpLink/actions/runs/40646151477) | #931 默认 PGO Client-only | 已独立复核12进程/354648ticks；Client-only inconclusive，原全产品NoGo保留；[报告](reviews/931-pgo-40646151477/INDEPENDENT-REVIEW.md) |
| [40642190610](https://github.com/SunSi12138/SharpLink/actions/runs/40642190610) | #732 三臂，36 processes/1.44M calls | 三个 contrast 的 T/U 全为 inconclusive；M1 Hold、M0 NoGo 保留 |
| [40636189267](https://github.com/SunSi12138/SharpLink/actions/runs/40636189267) | #933 旧整合头 | 保留 product/native 失败，不能删除 |
| [40642182557](https://github.com/SunSi12138/SharpLink/actions/runs/40642182557) | #933 产品修复头 50e608 | canonical 仅格式检查失败；后续 10b 只修格式 |
| [40644131003](https://github.com/SunSi12138/SharpLink/actions/runs/40644131003) | #933 当前头 10b | canonical 成功；#931 前置和 #933 功能性能门槛仍未解除 |

在可用 GitHub CLI 的云端环境下载官方原始工件：

```sh
gh run view 40646151477 --repo SunSi12138/SharpLink --json headSha,status,conclusion,jobs,url
gh run download 40646151477 --repo SunSi12138/SharpLink --dir artifacts/client-pgo-40646151477
```

需对应账号的仓库/Actions 读取权限。权限不足时使用证据分支；不要伪造下载成功或重跑实验来替代缺少读取权限。Actions 工件可能过期，长期接手以持久证据分支和固定源码提交为恢复入口。

## 必须从历史本机路径转换的命令

- 将文档中的 `/Users/sunsi/Documents/ChatGPT/SharpLink` 替换为云端实际 repo 根目录。
- 将 `.codex/worktrees/<任务>/SharpLink` 替换为上表对应分支创建的独立 checkout；不要在 dev 中直接套实验路径。
- 原 `/opt/homebrew/bin/python3` 是本机 Python 3.14。云端使用满足脚本要求的 Python，建议 3.12+。
- 原小爱/QQ脚本仅在用户 Mac 存在；云端不存在时直接向用户报告必要输入或完成结果，不因此中止工程任务。
- 所有代码和证据先按固定来源核对，再执行一次尚缺的步骤。不要重新提交已合并 PR，也不要重跑已完成的 NoGo/inconclusive campaign。

## 2026-10-28 云端接手更新

当前读取/写入/dispatch 路由和运行时缺口见 [接手进度](cloud-takeover-status.md)。GitHub connector 的 repository push/actions-write 权限元数据不等同于本机 `gh` 已认证或暴露了 workflow dispatch；缺少可用工具路由不是权限拒绝。未为探测权限创建提交、工作流或标签。

保存边界与当前状态见 [独立交接更新](independent-handoff-status.md)。此资料分支的文档更新不需要、也不应通过 PR 合入 dev；产品/工具PR的独立审核和测试门禁仍照常执行。

2026-10-28 11:54 UTC 补充：证据分支已追加为 `4fa383e71abfef508e2d2deacf7ec7daa6d7ba4b`，11归档、98,694,834字节；新增 `archives/r1-cloud-native-20261028.tar.gz`（SHA-256 `d38f97ee440ddd639e15a9e47774c8981584a374c85ccbecaa9adcbbc03c36cf`）保存001/002/003云端准备失败与native gate失败。原10归档字节不变，5a6仍是原实验的固定输入；新增证据不是性能Go。
