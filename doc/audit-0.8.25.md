# SharpLink 0.8.25 深度审核

English: [`en/audit-0.8.25.md`](en/audit-0.8.25.md)

以 0.8.24 commit `09d6078` 为基线，本批确认五项 P2 改进：不同 fully-qualified contract 的 sanitized Roslyn hint name 可碰撞并触发 CS8785；同 simple name 的 nested contract 生成同名顶层 peer；keyword method/parameter 丢失 `@` 后生成非法语法或错误的 `default` 表达式；by-ref 签名被错误接受；static method 及 abstract property/indexer/event 无法形成完整代理却缺少诊断。前两种表现按同一“生成标识不唯一”建议处理。

完整修复前 Generator 运行共 94 项，原有 88 项全部通过、新增六项恰好失败。修复后 hint name 追加已有 64-bit contract ID，公开且位于非泛型容器中的 nested contract 使用 containing-type identity 与短 hash 形成 peer name；所有源码 identifier 在 emission 边界转义，但 hash/Manifest 保持 raw symbol identity。`SHARPLINK052`–`SHARPLINK055` 覆盖 by-ref、static、非 method abstract member 与不可公开引用的 contract；泛型 containing type 继续归入 `SHARPLINK005`。带实现的默认 interface member 不会被当成 route。

补强后 Generator 96/96；精确最终树的非增量 Release 构建为 0 warning / 0 error，Unit 449/449、Integration 237/237、七包与全新缓存 package smoke 全部通过。40-contract/400-method 的 101-sample Generator 对照为 15.953 → 13.577 ms，quartile 重合；compiler-thread allocation 增加 40,976 B（0.14%），运行时热路径未修改。详见 [`performance-0.8.25.md`](performance-0.8.25.md) 与 [`migration-0.8.25.md`](migration-0.8.25.md)。
