# SharpLink 0.8.26 深度审核

English: [`en/audit-0.8.26.md`](en/audit-0.8.26.md)

以 0.8.25 commit `0773496` 为基线，本批确认五项 P2 改进：`[Oneway]` 接受带结果或 stream 的返回形状；用户参数可与 Proxy 内部 request/streams 局部变量冲突；仅大小写不同的 DTO 成员使 constructor mapping 的不区分大小写字典抛出重复键异常；生成字典读取器让 null key 泄漏为 BCL `ArgumentNullException`；非 public default interface helper 被错误建模为 RPC route。

完整修复前 Generator 运行共 100 项，原有 96 项全部通过、新增四项恰好失败。最初的第五项探针认为 collection count 的最小字节界限错误；源码追踪证明每个嵌套元素长度前缀固定为 UInt32，该假设不成立，因此删除探针与尝试性修改且不计入版本。替代探针随后证明 private helper 出现在 Proxy、Stub 和 Manifest，使修复前总数变为 101 项且恰好该项失败。

`SHARPLINK056` 现在限制 Oneway 为非泛型 `Task`/`ValueTask`。生成局部变量通过确定性后缀避开完整参数集合。DTO constructor mapping 先做 exact match，仅在唯一时才接受 case-insensitive fallback。字典 null key 在 `TryAdd` 前转换为 `DataLoss`。只有 public ordinary interface method 形成 route；非 public abstract method 复用 `SHARPLINK054`，有实现的 private helper 被忽略。

补强后 Generator 101/101；精确最终树的非增量 Release 构建为 0 warning / 0 error，Unit 449/449、Integration 237/237、七包与全新缓存 package smoke 全部通过。40-contract/400-method 的 101-sample Generator 对照为 14.755 → 13.530 ms；compiler-thread allocation 增加 76,640 B（0.27%）。16-key 字典保护独立对照为 171.891 → 170.941 ns。详见 [`performance-0.8.26.md`](performance-0.8.26.md) 与 [`migration-0.8.26.md`](migration-0.8.26.md)。
