# SharpLink 0.8.43 性能验证

English: [`en/performance-0.8.43.md`](en/performance-0.8.43.md)

Apple M4 / .NET SDK 10.0.102 上，以 exact 0.8.42 commit `cd2de157` detached checkout 与候选执行独立 Release 进程。Balanced TCP、单连接、并发 8、stream size 256；三组相邻进程交替顺序为 baseline/candidate、candidate/baseline、baseline/candidate，每个 stage 预热 1 秒、测量 3 秒，所有 stage 均零失败。

| workload | 0.8.42 QPS 中位数 | candidate QPS 中位数 | 配对中位变化 | P50 中位数 | P99 中位数 |
|---|---:|---:|---:|---:|---:|
| unary control | 163,931 | 163,672 | -0.6% | 49 → 49 µs | 69 → 70 µs |
| c2s | 7,910 | 8,028 | +1.5% | 1,004 → 981 µs | 1,387 → 1,354 µs |
| s2c | 8,568 | 8,584 | -1.8% | 943 → 942 µs | 1,084 → 1,103 µs |
| duplex | 4,824 | 5,019 | +4.0% | 1,626 → 1,580 µs | 2,560 → 2,199 µs |

独立 0.7.11/0.8.41 调查先以五进程发现 Balanced c2s/s2c/duplex 配对中位分别为 -4.3%/-7.3%/-14.8%，再把首个一致回退二分到 0.8.0。根因是 `RecordConsumed` 后对几乎总为空的跨流 credit queue 再取同一把锁；duplex size 256 约多 512 次锁。只移除空 drain 的三组因果实验为 +6.7%、+9.6%、+3.7%，配对中位 +6.7%，P50/P99 -6.2%，CPU/stream -8.8%。0.8.43 用 nullable queue fast path 保留跨流 credit 正确性，同时消除空锁。

早期 LoadTest 的 process-wide allocated bytes 除以完成 item 数曾显示 +20.7%，但专用 MemoryDiagnoser 未复现：size 32 为 6.57→6.58 KB，size 256 为 31.09→31.29 KB。该信号是吞吐下降造成的归一化假象，不作为产品 allocation 结论。0.8.43 fast path 不增加 per-item allocation。

原始 0.8.42/candidate JSON 与日志位于 `artifacts/performance/0.8.43-stream-ab/`；0.7.11 比较、二分、因果实验和 MemoryDiagnoser 证据保存在隔离性能任务的 `artifacts/`。组合门禁通过非增量 Release 0 warning/error、Generator 121/121、Unit 496/496、Integration 252/252、120 秒共享内存 Chaos 与 NativeAOT TCP；七包和 fresh-cache package smoke 随最终版本包验证。
