# Public control-plane failure contract

SharpLink 的 public control-plane API 不追求“零异常”。本页定义一个更窄的契约：**调用方在正常运行期间可以合理预期、并且需要机器可读分支的控制面结果，优先使用 structured result；programmer error、caller cancellation、内部 invariant、startup/terminal failure 和普通 RPC failure 继续使用 exception。**

这个规则只约束控制面和查询面，不改变 RPC error model，也不要求所有相邻 API 拥有相同返回类型。

## 决策规则

设计或审查一个 public control-plane API 时，按下面顺序判断：

1. 如果失败来自明显非法参数、非法 builder/configuration、caller cancellation、内部 impossible state、协议损坏、startup/terminal framework failure 或 runtime fatal failure，继续抛异常。
2. 如果输入本身有效，而失败只是当前生命周期、并发 publication、资源容量、远端可达性、peer capability 或其它正常运行状态导致 caller 需要选择“重试 / 放弃 / 降级 / 等待”，使用稳定 structured result。
3. 如果 operation 有 publication commit point 和后续 drain/cleanup，两者必须分开表达；cleanup 未完成不能把已经提交的 publication 伪装成 rollback。
4. 如果没有真实 caller 分支需求，不为了 API 对称性新增 `Try*`、结果类型或 generic result wrapper。

判断标准是“caller 是否把它当作正常业务/运维分支”，不是“代码里是否出现 `throw`”。

## Result 设计约束

Control-plane result 应保持小而稳定：

- 使用 operation-specific immutable result；共享 enum 只复用真正共享的低基数概念。
- `Succeeded` / outcome / failure code 可以机器分支；diagnostic message 只供人读，不是稳定协议。
- 不把 raw exception graph 放进 expected result。
- 不创建全框架 `Result<T>`、giant union 或 nullable 字段集合。
- caller cancellation 仍使用 `OperationCanceledException`，不转换成普通 failure code。
- parameter validation 仍使用标准 argument exception。
- internal invariant / impossible transition / protocol corruption 不降级成 expected result。
- result contract 不给 ordinary RPC hot path 增加 allocation、lock、branch 或 coordinator lookup。

## Publication 与 cleanup

对 mutation/update API，结果必须围绕真正的 commit point 建模：

```text
prepare + validate candidate
-> atomic publish
-> coordinator-owned drain / cleanup
```

Publication 前的 expected rejection 可以返回 `Succeeded = false` 和稳定 code；publication 后 caller cancellation 或 bounded cleanup timeout 不得回滚已经提交的 generation。需要时单独报告 `Published`、`ReferencesReleased`、`ForcedStop` 等状态。

## 当前 public surface 审计

| Surface | 当前/目标 contract | 结论 |
| --- | --- | --- |
| Multi-cluster `AddClusterAsync` / `ReplaceClusterAsync` / `RemoveClusterAsync` | operation-specific result + `SharpLinkClusterMutationFailureCode` | **已完成（#656）**。Add publication 与 readiness 分离；Replace pre-publication failure 与 old-slot retirement 分离；Remove 保留 bounded cleanup 状态。 |
| Runtime assembly `RegisterAssembly` / `ReplaceAssemblyAsync` / `UnregisterAssemblyAsync` | registration/replacement 已 structured；unregister 主要报告 reference-release/drain 状态 | **保持现有模型**。现有 API 的核心 caller 分支是 registration rejection 与 cleanup completion；本 issue 不为了统一命名重做 assembly result family。若未来出现必须区分 unregister rejection 原因的真实 caller，再开 focused issue。 |
| `ISharpLinkClient.CheckHealthAsync` / multi-cluster scoped health / Hosting health bridge | 当前普通 NotReady/Unavailable/Unsupported 仍可能通过 exception | **待 #655**。Health 是天然 query/result API，必须区分 remote health response 与 local probe/reachability outcome。 |
| Client runtime configuration publication：request timeout、retry、heartbeat/reconnect、endpoint admission、circuit breaker、compression、interceptor 等 | lifecycle closed / mode conflict / unsupported implementation 等仍可通过 `InvalidOperationException` / `NotSupportedException` | **待 #654**。为正常 publication rejection 建立 canonical non-throwing path；非法参数和 provider/invariant failure 继续 throw。 |
| Server runtime configuration publication：interceptor、response compression、admission/capacity 及后续 runtime controls | 与 Client 相同的 publication 边界 | **归 #654**。不在 #653 重复设计第二套 result family。 |
| Multi-cluster `GetClusterState` / `GetClusterRuntimeState` / `GetClusterReadiness` | configured-slot convenience getter；非法 key / 未配置 slot 当前抛 argument exception | **本轮不新增 `TryGet*`**。没有足够收益证明需要一套平行 status query surface；mutation caller 使用 #656 result，health caller 使用 #655 outcome。若未来 management-plane caller 需要 race-safe snapshot，再单独设计 cluster-status snapshot，而不是增加三个零散 `TryGet`。 |
| `Get<T>` / `GetWithMetadata<T>` proxy acquisition | lifecycle/routing/configuration precondition failure 使用 exception | **保持 exception**。这不是正常 control-plane rejection；缺失 route 或在 terminal lifecycle 创建 proxy 属于错误配置/错误使用。 |
| `StartAsync` / `ConnectAsync` / `WaitForReady*` / `WaitForShutdownAsync` / `StopAsync` | lifecycle、startup、terminal fault、caller cancellation | **保持 exception/lifecycle contract**。不把 startup bind/connect failure、terminal framework fault 或 waiter cancellation 包装成普通 result。 |
| `GetReadinessSnapshot` / `WaitForReadinessAsync` optional implementation capability | custom implementation 可 `NotSupportedException` | **保持现有能力边界**。Readiness primitive 不是 hot-reload publication；没有证据需要为 legacy/custom implementation 再增加 capability result。 |
| Ordinary RPC invocation | `SharpLinkException` / cancellation / protocol error | **明确不改**。#653 不重新设计业务 RPC error model。 |

## Multi-cluster query 的取舍

格式合法但已经被并发 Remove 的 cluster 对 management code 来说可能出现 race，但本轮仍不增加：

```text
TryGetClusterState
TryGetClusterRuntimeState
TryGetClusterReadiness
```

原因：

1. 三个 getter 都只是同一个 slot snapshot 的不同投影，分别加 `Try*` 会扩大 public surface，却没有形成更完整的 cluster status contract。
2. mutation 已经有 #656 的 machine-readable outcome；health/query reachability 由 #655 收敛。
3. 如果未来确实需要 race-safe inventory/status 查询，更合理的 API 是一个 immutable cluster-status snapshot（key + lifecycle/connectivity/readiness），而不是继续复制 getter family。

因此 valid-but-missing getter 目前仍视为 configured-slot precondition failure，而不是 #653 必须结果化的 expected outcome。

## Assembly registry 的取舍

`RegisterAssembly` 和 `ReplaceAssemblyAsync` 已经通过稳定 registration error code 表达 expected rejection；`UnregisterAssemblyAsync` 主要回答“framework-owned references 是否在 bounded drain 内释放”。这里不把 `ReferencesReleased = false` 强行扩展成另一个全能 failure union。

Multi-cluster scoped registry 仍要求目标 slot 已配置。明显非法 key 与未配置 slot 当前保持 precondition exception。若实际 control-plane consumer 需要把“slot concurrently removed”与“registration missing / drain pending”稳定区分，应新建 focused issue，扩展一个明确的 scoped registry/status contract；不要在 #653 里偷偷改变现有 result ABI。

## Exception 保留清单

以下类别应默认继续异常传播：

- `ArgumentNullException` / `ArgumentOutOfRangeException` / 明显非法 key、options 或 builder 配置；
- `OperationCanceledException`，表示 caller 自己取消等待；
- application callback / policy / provider 在 validation 或 execution 中真正抛出的异常；
- internal counter underflow、impossible generation/state、ownership invariant violation；
- malformed protocol / `DataLoss` / internal framework corruption；
- Server listener bind、local runtime materialization、Client startup/connect 的真实失败；
- Stop/terminal cleanup 需要向 caller 暴露的 unrecoverable framework failure；
- ordinary RPC 的 remote application/protocol failure。

不要为了“统一成 result”捕获这些异常再映射到 `Unknown`、`Rejected` 或 `Unavailable`。

## Expected-result 低基数概念

不同 subsystem 可以复用语义，但不要求共用一个 enum。常见 expected concept 包括：

```text
AlreadyExists
NotFound
Busy / PublicationConflict
LifecycleClosed
ModeConflict
CapacityExceeded
CandidateUnavailable
UnsupportedByPeer / UnsupportedByImplementation
NotReady / RemoteUnavailable
CleanupTimedOut / ForcedCleanup
```

只有实际 API 能稳定产生、caller 确实需要分支的 code 才应公开。Message 可以携带诊断上下文，但 telemetry/support snapshot 应优先记录低基数 code，不保存或聚合高基数异常文本作为稳定 contract。

## Focused implementation map

- **#656**：multi-cluster Add/Replace/Remove structured result — 已完成并进入 `dev`。
- **#654**：runtime configuration publication expected rejection — 待实现。
- **#655**：health probe NotReady/Unavailable/Unsupported structured outcome — 待实现。
- **#575**：support snapshot / diagnostics 应消费这些稳定低基数 classification，而不是从 exception message 反推状态。
- **#86**：最终 public API/package audit 负责记录 #654/#655/#656 产生的 source-breaking migration；#653 本身只固化 policy/audit，不增加 compatibility shim。

#653 不要求把这些 focused issues 合在一个 PR，也不因为它们尚未全部完成而重新实现其代码。

## Public API review checklist

新增或修改 control-plane API 时，review 必须回答：

1. 哪些 outcome 是合法输入下的正常运行状态？
2. caller 是否真的需要机器分支？如果需要，稳定 code 是什么？
3. 哪些 failure 属于 programmer error / cancellation / invariant / fatal，必须继续 throw？
4. publication commit point 在哪里？cleanup 是否与 publication outcome 分离？
5. message 是否仅用于 diagnostics，而不是机器分支？
6. custom/legacy implementation 的 unsupported capability 如何表达，是否真的需要 non-throwing canonical path？
7. 是否引入 generic result、重复 enum、nullable giant union 或 RPC hot-path 固定成本？
8. migration/XML/docs 是否明确 breaking return-type 或 exception-contract 变化？

只有在这些问题有清楚答案后，expected-failure result 才应成为 public contract。