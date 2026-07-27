# SharpLink 0.8.2 性能验证

English: [`en/performance-0.8.2.md`](en/performance-0.8.2.md)

基线为 0.8.1 commit `5d30863`，候选为 0.8.2 canonical VarUInt32 分支。Apple M4 / .NET 10.0.2 / BenchmarkDotNet 0.15.8；每项 3 个独立 launch、3 warmup、10 measurement iterations。普通连续 Request 是同轮主机控制项。

| Benchmark | 0.8.1 | 0.8.2 | 候选/基线延迟 | B/op |
| --- | ---: | ---: | ---: | ---: |
| ParseContiguousRequest（控制） | 39.32 ns | 40.23 ns | 102.31% | 0 → 0 |
| ParseContiguousMetadataRequest | 42.67 ns | 39.60 ns | 92.81% | 0 → 0 |

基线 metadata 的跨 launch 方差偏高，因此不宣称 7.2% 性能提升。关键结论是候选无分配，绝对延迟未回退，且 metadata/control 归一化比例由 1.085 降为 0.984。第一次从父目录启动的基准因检测到两个同名 project 而未产生测量，已明确排除；有效报告位于 `artifacts/performance/0.8.2-parser-ab/`。
