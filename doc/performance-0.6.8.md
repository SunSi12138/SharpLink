# SharpLink 0.6.8 性能证据

## P068-03：服务端用户调用 observer

### 环境与方法

- 机器：Apple M4，10 cores，Arm64。
- 系统：macOS 26.4.1。
- Runtime：.NET 10.0.2，Workstation Concurrent GC。
- A：`c2c6d53`，异步用户调用任务进入全局 `HashSet<Task>`，add/remove 都获取同一把锁。
- B：`c2c6d53` 加 P068-03 候选改动；框架任务仍显式持有，用户调用改由 active-call counter 与异常 observer 收敛。
- 配置：Release、TCP、Balanced、连接池 1/1、默认 30 秒 request timeout、并发 128。
- A/B 严格交替运行，各五次；每次 warmup 1 秒、采样 2 秒，取中位数。

### LoadTest 中位数

| 服务形态 | 指标 | A | B | 变化 |
|---|---|---:|---:|---:|
| 同步 `ValueTask` | QPS | 1,027,857.16 | 1,061,898.42 | +3.31% |
| 同步 `ValueTask` | P99 | 362 us | 374 us | +3.31% |
| 同步 `ValueTask` | P99.9 | 707 us | 691 us | -2.26% |
| `Task.Yield()` | QPS | 347,458.86 | 347,740.76 | +0.08% |
| `Task.Yield()` | P99 | 910 us | 896 us | -1.54% |
| `Task.Yield()` | P99.9 | 1,221 us | 1,325 us | +8.52% |
| 1 ms async | QPS | 89,141.37 | 88,285.43 | -0.96% |
| 1 ms async | P99 | 2,565 us | 2,546 us | -0.74% |
| 1 ms async | P99.9 | 2,915 us | 2,913 us | -0.07% |

三组共 30 个采样均为零调用错误。QPS 与 P99 通过 97%/105% 全局门禁。

同步服务不会进入改动前后的异步 observer，因此其 +3.31% QPS 只能视为本机运行波动，不能归因于 P068-03。目标 `Task.Yield()` 路径的 QPS/P99 没达到“可宣称性能收益”的 3%/5% 阈值；本任务不宣称平均吞吐收益。候选实现仍保留，因为代码路径直接证明每个异步用户调用的全局任务集合 add/remove、两次锁获取和 `ContinueWith` 已消失，同时框架任务继续被持有、等待并观察异常。1 ms async 没有越过回归门禁。

`Task.Yield()` 的短样本 P99.9 中位数增加 8.52%，而 P99 改善 1.54%；P99.9 不在当前全局回归阈值内，但该离群波动必须在 0.6.8 最终较长 Gate 中复测，不能用于宣称改善。

原始 JSON 保存在本次实施机 `/tmp/sharplink-p06803/`，每份包含 commit、OS、CPU、Runtime、GC、Transport、Profile、连接池、timeout、成功数和延迟分位数。

## P068-04：附加实验

待专项审计完成后补充。没有达到证据阈值的候选不会修改实现。
