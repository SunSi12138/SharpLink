# SharpLink 0.8.0 深度审核

English: [`en/audit-0.8.0.md`](en/audit-0.8.0.md)

本批按“每五项 P2 及以上实证改进形成一个小版本”执行。基线为 tag `v0.7.11` 的 commit `0151db10c89c8067859daef06ef04e2905cd0e89`。候选项必须先以失败测试或可观测状态复现，再修复和回归；已存在明确诊断的 control-only overload collision 因此被排除，没有重复建设。

| 等级 | 问题与实证 | 修复 | 验证 |
| --- | --- | --- | --- |
| P2 | 固定长度 Codec、字符串和原生集合只检查“至少足够”，会静默忽略尾随字节；分段路径还可能抛出不同异常。修复前 Unit 335 项中相关 4 项失败。 | 所有内置固定长度值要求精确长度；字符串和原生集合按前缀计算并要求完整消费，空/null 分支同样校验。 | 连续/分段、截断/尾随、null/empty/positive 全部覆盖，统一为 `DataLoss`。 |
| P2 | `bool` 把任意非零字节解释为 true；可空标记也把任意非零解释为 present。修复前新增标记测试失败。 | 仅接受 Boolean `0/1`；`bool?` 接受 `0/1/255`；其他可空值 presence 仅接受 `0/1`。 | 24 类可空内置值同时覆盖连续和分段非法标记。 |
| P2 | connection batching 达阈值时只返回触发 stream 的 credit，其他 open stream 的 pending credit 被留在 connection 总量中，可能阻止完整窗口借用。修复前流控状态断言失败。 | 阈值触发时一次提取所有贡献 stream；触发项直接返回，其余项进入会话排空队列并各发一个 `WindowUpdate`。 | 控制器精确 identity/credit/不重复断言，以及真实会话输出的两份 WindowUpdate 帧断言。 |
| P2 | Generator 只读取标记接口自身的 `GetMembers()`，普通基接口方法不进入诊断、DTO roots、fingerprint、proxy 或 stub。修复前 emitted-source 测试缺少继承方法。 | 统一收集直接与继承的 ordinary methods，并按可调用签名优先保留最派生声明。 | 纯继承方法生成 proxy/stub；直接重声明只生成一次；Generator 81/81。 |
| P2 | 所有 unmanaged 参数都被原生 blit；user-defined struct 或 nullable 即使被 Adapter/Codec 选中也会绕过 Codec，与 manifest 的 LengthDelimited 声明不一致。修复前生成代码没有 Codec 字段和调用。 | 仅框架固定内置类型保持 inline；user-defined/nullable 值统一走 provider-selected Codec 和 length-delimited framing。 | 生成代码同时断言 `GetCodec<Point>()` 和 `Serialize` 调用；Generator 81/81。 |

断言质量复核没有 assertion-free 或仅 trivial 的新增测试。伪变异复核额外捕获并修复了两个缺口：会话层漏发额外 credit，以及继承方法直接重声明重复生成。

完整性能数据见 [`performance-0.8.0.md`](performance-0.8.0.md)，迁移影响见 [`migration-0.8.0.md`](migration-0.8.0.md)。
