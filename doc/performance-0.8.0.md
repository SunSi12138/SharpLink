# SharpLink 0.8.0 性能验证

English: [`en/performance-0.8.0.md`](en/performance-0.8.0.md)

## 环境与方法

- 基线：`v0.7.11` / `0151db10c89c8067859daef06ef04e2905cd0e89`；候选：0.8.0 第一批审核修复。
- macOS Tahoe 26.4.1、Apple M4 arm64、.NET SDK 10.0.102、Runtime 10.0.2、Concurrent Workstation GC。
- BenchmarkDotNet 0.15.8，1 launch、3 warmup、10 measurement iterations；同一 checkout、同一机器、相同参数顺序复测。
- 表中为 Median；越低越好。报告保存在任务目录 `artifacts/performance/`，不纳入包。

| 场景 | 0.7.11 | 0.8.0 | 延迟比例 | B/op |
| --- | ---: | ---: | ---: | ---: |
| PendingRegisterAndComplete | 39.692 ns | 39.902 ns | 100.53% | 0 → 0 |
| ParseContiguousRequest | 26.342 ns | 24.667 ns | 93.64% | 0 → 0 |
| ParseContiguousMetadataRequest | 26.648 ns | 24.807 ns | 93.09% | 0 → 0 |
| ParseSegmentedMetadataRequest | 258.993 ns | 256.507 ns | 99.04% | 0 → 0 |
| CreatePushAndRestoreCallContext | 22.016 ns | 21.604 ns | 98.13% | 128 → 128 |
| CreateDeadlinePushAndRestoreCallContext | 21.677 ns | 21.179 ns | 97.70% | 128 → 128 |
| PushAndRestoreCallContext | 18.582 ns | 18.886 ns | 101.64% | 72 → 72 |

所有分配保持不变。唯一较慢的点为 +1.64%，候选测量呈多峰且改动不在该调用路径，判定为本机噪声；其余六点持平或更快。严格长度判断保持单次常量比较；跨 stream credit 仅在 connection batching 阈值触发时执行额外工作，正常未达阈值与单 stream 阈值路径不分配额外对象。

结论：本批没有性能回退证据。结果只代表本机 macOS arm64；没有运行远程跨平台 runner。
