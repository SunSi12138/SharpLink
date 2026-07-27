# SharpLink 0.8.37 性能验证

English: [`en/performance-0.8.37.md`](en/performance-0.8.37.md)

本批只修改 Source Generator 的无效模型拒绝边界，以及 generated DTO 的局部变量命名；Runtime、Client、Server 和有效 DTO 的执行语句/布局均未改变，因此不存在新的运行时热路径。

在 Apple M4 / .NET SDK 10.0.102 上，使用独立 exact `e4bf5f1` worktree 与候选交错运行同一个 `demo/HostApplication` 非增量 Release build，各五次并复用同一任务级 NuGet cache：

| 构建 | 五次 wall time | 中位数 |
|---|---|---:|
| 0.8.36 exact baseline | 4.95 / 2.28 / 2.13 / 1.95 / 1.98 s | 2.13 s |
| 0.8.37 candidate | 2.96 / 2.00 / 1.89 / 1.88 / 1.87 s | 1.89 s |

候选中位数减少 0.24 秒（−11.3%），没有 Generator/build 性能回退。首对包含 baseline worktree 的冷文件/编译缓存成本，但纳入或排除该对都不会改变“不回退”结论。精确基线保留在 `artifacts/performance/0.8.37-baseline/`。

组合门禁为非增量 Release 0 warning/error、Generator 113/113、Unit 483/483、Integration 240/240。120 秒共享内存 Chaos 为 866,582 success / 337,510 expected / 0 unexpected / 23 restarts，Client/Server Error 为 0、drain 与五项零指标通过；NativeAOT TCP smoke、七包预提交打包与 fresh-cache functional package smoke 通过。
