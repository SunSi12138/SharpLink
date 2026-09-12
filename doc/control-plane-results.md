# Control-plane result and exception contract

本文固定 SharpLink 公开 control-plane API 在“返回结构化结果”和“抛异常”之间的边界。目标是让 orchestration、健康探测和动态配置代码不需要依赖异常消息做正常分支，同时保留参数、配置和内部故障的异常语义。

## Review rule

| 情况 | 公开契约 | 典型示例 |
| --- | --- | --- |
| 预期运行时拒绝或状态竞争 | structured result / `Try...` | cluster 已存在、cluster 不存在、route 冲突、合法 cluster 在查询前被并发移除 |
| 参数或配置错误 | exception | default/非法 `SharpLinkClusterKey`、非法 timeout、无效 builder 配置 |
| caller cancellation | `OperationCanceledException` | 调用方取消等待或 mutation |
| 内部 invariant / 非预期运行时故障 | exception | 实现 bug、资源清理异常、未预期 transport/runtime failure |

结构化结果必须提供稳定的 typed code/status；调用方不应解析异常消息或日志文本来判断 expected runtime outcome。反过来，也不要求把所有异常都转换为 result：programmer error、invalid configuration、cancellation 和 invariant failure 继续保持异常语义。

## Multi-cluster mutations

`AddClusterAsync`、`ReplaceClusterAsync` 和 `RemoveClusterAsync` 使用 operation-specific structured result 表达预期拒绝和 publication/cleanup outcome。调用方应根据 `Succeeded`、failure code 以及 publication/cleanup 字段分支，而不是捕获 `InvalidOperationException` 再解析消息。

Mutation result 只描述该 control-plane operation 的结果，不承诺远端 cluster 已 Ready。需要远端可用性时仍应显式使用 readiness API。

## Cluster status query

当 cluster 是否仍存在本身就是运行时状态的一部分时，使用 `TryGetClusterStatus`：

- 合法且当前存在的 key 返回 `true`，并给出一个 `SharpLinkClusterStatusSnapshot`；
- 合法但当前不存在的 key（包括查询前刚被并发移除）返回 `false`；
- default 或非法 key 是 programmer error，仍抛 `ArgumentException`。

`SharpLinkClusterStatusSnapshot` 是 point-in-time、level-triggered 观察。读取之后 cluster 可以立即因为 replacement、removal、disconnect 或 lifecycle transition 改变状态，因此它不是 lease，也不是后续操作成功的保证。

`GetClusterState`、`GetClusterRuntimeState` 和 `GetClusterReadiness` 保留为 convenience getter。当调用方把“cluster 必须存在”视为自身 invariant 时可以继续使用它们；cluster 缺失时这些 getter 仍可以抛异常。需要处理正常存在性竞争的 orchestration 代码应使用 `TryGetClusterStatus`。

## Audit scope and follow-up boundaries

本契约只统一 expected runtime outcome 的建模规则，不把相邻问题合并成一个大改动。以下行为保持独立演进：

- coordinator running 时新增 cluster 的 readiness / publication 语义；
- runtime configuration update 的 structured result；
- health-check API 的 structured result。

这些能力可以在各自实现中复用同一条 review rule：expected runtime state 使用 typed result/status，调用方错误和非预期故障继续使用异常。这样可以避免为了“消除异常”而扩大热路径、改变 RPC wire contract，或把互不相关的 control-plane 行为耦合在一次变更中。
