# 云端接手进度：2026-10-28 11:18 UTC

本页是已完成事实和下一动作，不是发布批准。继续以正式发布3.0为目标；不缩小原合同，不提高性能容忍度，不删除不利证据。每个新增产品/工具/文档候选仍由未参与实现的代理独立审核精确HEAD。

## 已恢复的事实

- 已读取固定交接 `ba99c6f9467fd85233afbc0dc1c84cf839e1b5a0` 的完整入口、同目录附录、发布门禁、决策树及证据索引。原有 checkout 检查为干净并保留；另建干净云端 checkout，不依赖 `/Users/sunsi`。
- 远端 `dev=80965d5de2bd7bc4979bb36f3f2811493ade4fd0`；`main=ab8654783d1af35172fc52fea8423dd6ad41b44c`。这只是观察时的基线，不是最终冻结候选。
- 证据分支 `5a6cf6c4` 的10归档全部按index校验SHA-256与大小，合计95,369,648字节。472排除项为391缓存、78可能含环境变量的MSBuild binlog、3私有进程快照；M1仅移除manifest的`environment.processSnapshot`，不把受污染实验改称干净。原计时数据和失败保留。
- #931默认PGO已[独立复算](reviews/931-pgo-40646151477/INDEPENDENT-REVIEW.md)：12进程、354648次RPC/ticks，无失败或不完整pair；吞吐+0.2591%、CPU/op−0.1234%、P99+1.1567%、P99.9+4.7347%，对应描述性95%区间均跨1。第三对不利样本保留，Client-only结论inconclusive，**原全产品NoGo不变，PR931保持Draft**。
- #933当前头`10b1bf03`的[canonical40644131003](https://github.com/SunSi12138/SharpLink/actions/runs/40644131003)已核对head并确认success；fast/extended、两架构reader allocation、两runtime的producer/native和enabledTLS均成功。PR正文仍写旧16ef失败，不应覆盖当前远端事实；旧失败也不删除。原始native companion、实际测试库存和功能性能的独立验收尚待完成。
- #955在原`ba99c6f9`有PR Fast40677288021和CodeQL40677288105成功；这不自动审核本次文档更新。#931/#933尚未合并；#732是open issue而非PR。929/805/775/732/728/387/253均继续open；长期tracker开放本身不是新增发布阻断，范围以实际承诺与明确决策为准。

## 按顺序推进

1. #931下一步仅为R1（产品94adeae8，报告87f76fb8）对ac17的**有界、非计时、实际TCP Tier1机制比较**。触发依据是已看到真实TCP候选重复`hasNext`，不是声称已归因尾延迟。保持共同candidate DLL、private10.0.13、4352操作与四次固定暂停、四个真实优化Tier1目标；工具精确HEAD独立审核后执行。不得同时叠R2、扩大warmup、追加计时或由R1/ac17改善推断原baseline恢复。
2. 之后按原决策树推进#732阶段漂移诊断和#933原始门禁审核。已完成的三臂、FullOpts、PGO实验不重跑；#732 M0 NoGo/M1 Hold保留，#933不得绕过#931前置。
3. 最终集成/API台账、真实双版本wire控件、三OS packaged trim、full矩阵调度与正式配对接受、allocation/recorder、10分钟soak/ABI、安全/迁移及finalmain重验仍未完成。使用[完整发布手册](release-gates-and-merge.md)，不把当前canonical成功当release-ready。

## 云端工具与资源边界

- 已有SDK10.0.102、runtime10.0.2、Python3.12.14可用；dotnet原不在PATH，使用已有安装即可，不代表新候选已restore/build通过。#931要求隔离官方10.0.13，#732要求10.0.11，不能用roll-forward换运行时。actionlint/dotnet-trace/nativeAOT编译工具未在本次PATH发现。
- 仓库没有AGENTS.md/.agents/skills，当前executor也未找到`dotnet-test:run-tests`技能。没有声称执行过该技能或新的.NET测试；下一执行者应先检查新增可用技能并遵守nativeMTP/TUnit语法及精确执行数量。
- public git clone/fetch成功；`gh`没有认证，未找到Git credential helper。对不变handoff ref的一次`git push --dry-run`因无法读取GitHub用户名失败，无远端修改。GitHub connector可读并报告contents/actions/workflows写权限，但现有工具没有workflow_dispatch；GET fetch不提供写操作。**这是尚未解决的执行路由缺口，不是权限拒绝。**未添加凭据、改变安全策略、伪造触发器或创建试跑标签。
- 现有public仓库standard GitHub-hosted runner计算免费；larger runner始终计费。账号实际并发/存储预算不可由当前connector证实；没有购买资源或提高额度。[GitHub费用说明](https://docs.github.com/en/billing/concepts/product-billing/github-actions)。full三OS共378分片、纯计时369.792 runner-hours，不含baseline/编译/失败和最终main重验；分OS矩阵/运行，避免单matrix超过256jobs，每job上限6小时。[GitHub限制](https://docs.github.com/en/actions/reference/limits)。
- #954的`performance-matrix.py run-shard`已通过provenance hook保存SDK/CPU/实际DLL或AOT身份；只跑裸shell不获得这些receipt。validator明确`performanceDecision:not-assessed`，结构完整不代替性能接受。
- NuGet Trusted Publishing实际policy仍须在NuGet账户确认，不是已知缺失。代码期望repo `SunSi12138/SharpLink`、workflow `release-gate.yml`、environment `release`与九包owner匹配；不读取secret值或切换长期token。没有tag、release、NuGet发布或管理员配置变更。
