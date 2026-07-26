# SharpLink 0.8.1 深度审核

English: [`en/audit-0.8.1.md`](en/audit-0.8.1.md)

本批以 0.8.0 commit `7a99fc6` 为基线，五项 P2 及以上改进均先取得失败测试或分配实证。理论大端平台的 native payload 重写因不在当前支持矩阵、wire 风险与工程收益不匹配而明确排除。

| 等级 | 问题与实证 | 修复与验证 |
| --- | --- | --- |
| P1 | `SharpLinkAuthenticationContext.Scopes` 实际返回 `HashSet`；调用方可以注入授权 scope，空 context 还共享同一个可变静态 set。 | scope 规范化后转为 `FrozenSet`；mutation probe 同时覆盖单实例提权和跨实例污染。 |
| P2 | `SharpLinkEndpointSnapshot` 与 generated assembly/cluster manifest 以 `IReadOnlyList` 暴露真实数组，强转后可改写 topology、contract、Codec 或 route。 | 所有顶层集合和嵌套 method/service dependency 数组均由一次性只读 wrapper 暴露；Generator emitted-source 断言覆盖完整层级。 |
| P2 | Delegate/DNS Resolver 的 `DisposeAsync` 同步执行 cancellation callback，且只 Cancel 不 Dispose owned CTS；Analyzer 同时报出两处 `CA2213`。 | 引入每实例共享的 disposal Task，使用 `CancelAsync`，完成后 Dispose；operation admission 与 dispose 使用同一 gate，避免 dispose race。 |
| P2 | generated request 对 Boolean 和语义 struct 使用 `Unsafe.ReadUnaligned`，绕过已有 `Bool/Decimal/DateOnly/DateTime/DateTimeOffset/TimeOnly/Rune` 等 Codec 校验。 | Boolean 保持 1-byte inline wire，但 encoder 规范化且两个 decoder 只接受 `0/1`；其他需要语义校验的 fixed values 改走 length-delimited built-in Codec。普通整数、浮点、Half、Guid、TimeSpan、Int128/UInt128 仍 inline。 |
| P2 | `BlitListCodec<T>` 先分配 `T[]`，再由 `new List<T>(array)` 分配第二个数组并完整复制。 | 使用 `CollectionsMarshal.SetCount/AsSpan` 直接写 List-owned storage；既有 null/empty/positive、连续/分段与 malformed 测试全通过。 |

修复前 Unit 339 项中 3 项失败，Generator 83 项中 2 项失败。修复后断言质量与伪变异复核补充了 Boolean encoder、nested service dependencies 和 cluster route wrapper，避免“只保护一半”的实现。性能见 [`performance-0.8.1.md`](performance-0.8.1.md)，wire 迁移见 [`migration-0.8.1.md`](migration-0.8.1.md)。
