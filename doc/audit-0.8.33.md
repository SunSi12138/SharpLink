# SharpLink 0.8.33 深度审核

English: [`en/audit-0.8.33.md`](en/audit-0.8.33.md)

以 0.8.32 commit `2f3d27c` 为基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | 两个继承接口可声明参数完全相同但返回类型不兼容的方法；Generator 只按参数折叠，随后生成的类无法实现两个成员。 | 新增 `SHARPLINK057`，在生成前拒绝冲突并抑制损坏的 Proxy/Stub。 |
| P2 | 两个不同 enum 全名可被标点替换规则压成同一 Stub size-field 标识，产生重复字段。 | 字段名追加基于原始完整类型名的确定性 64 位后缀；wire type、route hash 与 payload 不变。 |
| P2 | 同步 Builder 失败回滚直接阻塞等待任意 `DisposeAsync`；清理若捕获非泵送 `SynchronizationContext`，Build 永不返回。 | Client/Server 的同步回滚通过隔离的异步任务完成清理，继续同步返回并保留主异常与清理异常。 |
| P2 | Client Hosted Service 第二次 Start 会覆盖已有 `_client`，随后 accessor publication 失败并把替换实例送入清理，原 owner 丢失。 | 生命周期锁内识别重复 Start，并在通用启动失败清理之外拒绝，保留现有 client 与 accessor。 |
| P2 | Multi-Cluster Hosted Service 对 coordinator 独立存在同类 owner 丢失问题。 | 为 Multi-Cluster 生命周期加入独立的重复 Start 边界与回归测试。 |

预修复 Generator 104 项中原有 102 项全部通过，仅两个新探针失败；Unit 477 项中原有 474 项全部通过，仅三个新探针失败。修复后的断言与伪变异复核覆盖诊断数量、损坏产物抑制、字段标识唯一性、原异常保留、清理完成、owner 保持与 accessor 不被污染。

极限 DNS jitter 猜想经可执行探针否决：受支持的 .NET 运行时把超范围浮点到整数转换饱和而非负向回绕，因此没有缺陷也未修改生产路径。最终非增量 Release 构建为 0 warning / 0 error，Generator 104/104、Unit 477/477、Integration 238/238、七包与全新缓存 smoke 全部通过。性能证据见 [`performance-0.8.33.md`](performance-0.8.33.md)，行为说明见 [`migration-0.8.33.md`](migration-0.8.33.md)。连续无新改进轮次仍为 0/3。
