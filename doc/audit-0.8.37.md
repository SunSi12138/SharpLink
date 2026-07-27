# SharpLink 0.8.37 深度审核

English: [`en/audit-0.8.37.md`](en/audit-0.8.37.md)

以 0.8.36 commit `e4bf5f1` 为精确基线，本批确认五项 Generator P2 改进，并在最终门禁中追加一项 P2 测试可靠性修复。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | private/protected/private-protected/file-local Service 或显式 DTO 可进入生成模型，但 sibling `SharpLink.Generated` 代码无法访问；真实编译产生两处 generated-manifest `CS0122`。 | 沿完整 containing-type 链验证生成代码可达性；Service 报 `SHARPLINK018`，DTO 报 `SHARPLINK009`，同程序集 internal/protected-internal 仍允许。 |
| P2 | DTO 成员 `@class` 先被转义再拼入 `local_@class`/`seen_@class`，生成非法 C#。 | 模型保留原始符号名；只有 `value.@class` 与 initializer member access 执行转义，组合局部标识符使用原名。 |
| P2 | 非密封 record 是普通非密封 class 的唯一例外；以 base record Codec 发送 derived record 会只写 base schema，并解码成 base instance，静默切掉派生状态。 | Native DTO class（包括 record）统一要求 sealed；多态图改用显式 Codec Adapter。 |
| P2 | ref struct 被 unmanaged 快路当作内置值，但 Proxy/Stub 会把它放进普通字段与 `IRpcCodec<T>` 泛型位置，生成代码无法编译。 | 在 unmanaged 短路前报告 `SHARPLINK009`，契约有效性检查同步抑制 Proxy/Stub。 |
| P2 | static abstract operator/conversion 不属于 ordinary RPC method，也未被 unsupported-member 检查捕获；生成 Proxy 无法实现接口。 | `SHARPLINK054` 覆盖抽象 operator/conversion，并在发射前抑制损坏 Proxy/Stub。 |
| P2 | 0.8.36 admission/drain 竞态门禁用 volatile store 模拟生产的 `Interlocked.Exchange` 状态切换；ARM64 进程级高负载下捕获到 store-buffering 假阳性。 | 动态探针改用同签名 `Interlocked.Exchange(ref int, int)` 并保留 192,000 调度扫描，使测试与生产线性化边界一致。 |

预修复完整 Generator 共 113 项：原有 108 项全部通过，五个新增探针恰好失败。精确基线的可执行 record 探针输出 `RECORD_SLICE_PROVEN runtime=DerivedPayload decoded=BasePayload value=7`，确认不是理论风险。修复后 113/113；断言与伪变异复核覆盖拒绝/允许可达性、keyword local/member 分离、record sealed 边界、ref-like 诊断与产物抑制，以及 operator 诊断与产物抑制。最终并行门禁令旧 race probe 产生一次假 witness；改用生产等价原子交换后，三个连续完整 Unit 复跑均为 483/483。

本批没有修改 Runtime/Client/Server，也不改变任何有效生成产物的运行时路径。精确 0.8.36 worktree 与候选交错执行同一 HostApplication 非增量 Release 构建，各五次 wall time 中位数为 2.13 -> 1.89 秒，没有构建性能回退；详见 [`performance-0.8.37.md`](performance-0.8.37.md)。

最终 120 秒共享内存 Chaos 完成 866,582 次成功调用、337,510 次预期注入故障与 23 次重启，0 次意外失败、Client/Server Error 均为 0；排空成功且五项活跃指标全部为 0。独立 NativeAOT 输出 `AOT_SMOKE_PASS transport=tcp`。非增量 Release 构建为 0 warning / 0 error，Unit 483/483、Integration 240/240；七个 0.8.37 包预提交打包与 fresh-cache TCP/shared-memory/static/dynamic functional smoke 通过。本轮仍发现新改进，连续无新改进轮次保持 0/3。
