# SharpLink 0.8.44 性能验证

English: [`en/performance-0.8.44.md`](en/performance-0.8.44.md)

Apple M4 / .NET SDK 10.0.102 上，以 exact 0.8.43 commit `9789fbe` detached checkout 与候选执行独立 Release 进程。Balanced TCP、单连接、并发 8、stream size 256；三组完整 unary/c2s/s2c/duplex 相邻进程交错样本全部零失败。

短样本中 c2s 三组变化方向一致但分配/CPU 归一化同时大幅反向波动，不足以判断回退。追加五组严格相邻且反转顺序的 c2s 对照，每个进程预热 2 秒、测量 10 秒：

| 指标 | 五组配对中位变化 | 结论 |
|---|---:|---|
| QPS | -0.05% | 无可测吞吐回退 |
| P50 | -0.19% | 稳定 |
| P99 | +0.27% | 稳定 |
| CPU/operation | -0.38% | 稳定 |

五组 QPS 变化依次为 -1.06%、+1.07%、+0.47%、-0.05%、-0.07%，跨过零且中位接近零。process-wide allocation/完成数仍受启动、后台工作与吞吐分母影响，只作辅助信号，不宣称分配优化。

本轮修改集中于 shutdown 和 terminal failure cleanup。正常 unary、stream item 编解码与 send-pump enqueue 热路径没有新增对象；terminal stream closure 仅增加结构化 `finally`。原始 JSON 与日志位于 `artifacts/performance/0.8.44-stream-ab/` 和 `artifacts/performance/0.8.44-c2s-long-ab/`。

组合门禁通过非增量 Release 0 warning/error、Generator 121/121、Unit 503/503、Integration 252/252、120 秒共享内存 Chaos、独立进程 SharedMemory NativeAOT、七包 pack 与 fresh-cache PackageSmoke。
