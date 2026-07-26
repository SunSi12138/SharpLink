# SharpLink 0.8.1 性能验证

English: [`en/performance-0.8.1.md`](en/performance-0.8.1.md)

## `List<T>` 交替 A/B

- 基线：0.8.0 commit `7a99fc6`；候选：0.8.1 direct-to-List decode。
- macOS Tahoe 26.4.1、Apple M4 arm64、.NET SDK 10.0.102 / Runtime 10.0.2。
- BenchmarkDotNet 0.15.8，4096 invocations、3 warmup、10 measurement iterations、1 launch。
- `baseline-1/candidate-1/.../baseline-3/candidate-3` 严格串行交替；表中取三轮 Median 的中位数。

| Payload | 0.8.0 median | 0.8.1 median | 候选延迟比例 | 候选吞吐比例 | B/op |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 16 integers | 87.167 μs | 87.553 μs | 100.44% | 99.56% | 560 → 472 (-15.7%) |
| 256 integers | 91.167 μs | 88.915 μs | 97.53% | 102.53% | 2480 → 1432 (-42.3%) |

候选消除的 88/1048 bytes 与中间数组大小吻合；两档吞吐均高于 97% 门禁。第一次非交替候选因 checkout 内存在两个同名 benchmark project 被 BenchmarkDotNet 拒绝，另一次隔离构建的 `csc` 异常退出（code 139）；二者均无测量数据且未计入结果。有效原始报告保存在任务目录 `artifacts/performance/0.8.1-alternating/`。

结论：allocation 显著下降，未发现吞吐回退。结果只代表本机 macOS arm64。

## 运行时哨兵

七条非本批改动路径的 runtime hot-path benchmark 也完成运行，所有 B/op 与 0.8.0 完全一致。其绝对延迟整体同时上移约 1.5 倍（包括互不相关的 pending table、frame parser 与 call context），表明当时存在主机级频率/负载偏移，因此不把这次非交替绝对值用于回归判定。0.8.1 唯一修改的稳态数据路径 `BlitListCodec<T>` 使用上面的三轮严格交替 A/B 作为门禁。
