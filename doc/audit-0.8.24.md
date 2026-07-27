# SharpLink 0.8.24 深度审核

English: [`en/audit-0.8.24.md`](en/audit-0.8.24.md)

以 0.8.23 commit `3202bd7` 为基线，本批确认五项 P2 改进：C# attribute 常量绕过 `TimeoutAttribute` 构造器校验；union 接受非正 tag；union case 接受抽象、开放、无关或多 tag 映射；显式空 `SharpLinkRpcContracts` 筛选错误回退到自动引用扫描；两种生成 Manifest 长期误报 generator `0.8.3`。

完整修复前 Generator 运行共 88 项，原有 82 项通过、六项恰好失败（generator version 在 JSON 与 assembly Manifest 两个独立表面各有一项）。修复新增 `SHARPLINK050` 和 `SHARPLINK051`，非法 timeout 契约不再生成危险 descriptor；union 保证正 tag 到可赋值闭合 concrete case 的一对一映射；显式空筛选返回空集合；版本来自执行中的 Generator assembly，消除后续发布手工同步点。同一方法验证路径中顺带删除了一个恒为 false 的冗余分支，此项按 P3 清理，不计入五项版本推进条件。

修复后非增量 Release 构建为 0 warning / 0 error，Generator 88/88、Unit 449/449、Integration 237/237、七包打包与全新缓存 package smoke 全部通过。初版独立 timeout 分析管线令 400-method synthetic workload 从 57.290 ms 增至 69.062 ms，已否决；并入现有方法诊断遍历后，101-sample 对照为 41.029 → 40.675 ms，compiler-thread allocation 增加 0.57%。运行时热路径未修改。详见 [`performance-0.8.24.md`](performance-0.8.24.md) 与 [`migration-0.8.24.md`](migration-0.8.24.md)。
