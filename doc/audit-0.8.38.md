# SharpLink 0.8.38 深度审核

English: [`en/audit-0.8.38.md`](en/audit-0.8.38.md)

以 0.8.37 commit `576fbe3` 为精确基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | 生成 Service activator 把所有构造依赖都当作可经 `IServiceProvider.GetService` 返回的普通值；合法 `ref`、ref-like 与 `ref readonly` 依赖在 generated Manifest 产生 `CS1620`、`CS0030`、`CS9193`。 | 选择后验证构造参数；需要 ref storage、ref-like、pointer/function-pointer 的依赖报告 `SHARPLINK019` 并抑制对应 Service descriptor，普通值/引用与 `in` 参数不变。 |
| P2 | `[RpcIgnore]` 的 C# `required` 成员未进入 DTO 模型，generated object initializer 漏设成员并产生 `CS9035`。 | 构造计划覆盖全部 compiler-required 成员和 required field；`SetsRequiredMembersAttribute` 保持有效，无合法计划时报 `SHARPLINK012`。 |
| P2 | DTO 构造器匹配名称与类型但忽略 `RefKind`，会选择 `ref`/`ref readonly` 构造器后按普通表达式调用，generated Codec 产生错误或警告。 | 要求 `ref`、`out`、`ref readonly` storage 的构造器不参与选择；若存在普通值构造器则继续生成，否则报告 `SHARPLINK012`；`in` 保持有效。 |
| P2 | pointer/function-pointer 在 unsupported 检查前被 `unmanaged` 快路接受，既没有 `SHARPLINK009`，也继续生成含非法泛型与 unsafe 位置的 Proxy/Stub；真实编译产生 10 个 `CS0214`/`CS0306`。 | 在 unmanaged 短路前报告两项 `SHARPLINK009`，契约有效性同步抑制 Proxy/Stub。 |
| P2 | interceptor 抛出 `SharpLinkException(Cancelled)` 时，Context 同时记录 `Status=Failed` 与 `ErrorCode=Cancelled`。 | Client terminal/pipeline、Server pipeline/exception mapping 统一把 `OperationCanceledException` 与结构化 Cancelled 错误记为 `SharpLinkInvocationStatus.Cancelled`。 |

预修复完整 Generator 的 113 个既有测试全部通过，四个新增回归测试恰好失败；独立 interceptor 探针也只在错误状态断言失败。修复后 Generator 117/117、目标 interceptor 1/1。三个真实无效工程只剩精确的 2×`SHARPLINK019`、2×`SHARPLINK012`、2×`SHARPLINK009`，不再出现原生 C# 错误；覆盖 `in` DI、`SetsRequiredMembers` 和备用值构造器的正向工程为 0 warning / 0 error。

精确基线与候选交错执行 HostApplication 非增量 Release 构建，中位数 1.97 -> 1.92 秒；三进程 interceptor RPC 中位数 41.848 -> 41.831 µs，分配均为 1,584.03 B/op。完整数据见 [`performance-0.8.38.md`](performance-0.8.38.md)。

最终非增量 Release 构建 0 warning / 0 error，Generator 117/117、Unit 483/483、Integration 241/241。120 秒共享内存 Chaos 完成 878,800 success、341,743 expected、0 unexpected、23 次重启，Client/Server Error 为 0，drain 与五项零指标通过；NativeAOT TCP 输出 `AOT_SMOKE_PASS transport=tcp`。本轮仍发现新改进，连续无新改进轮次保持 0/3。
