# SharpLink 0.6.7 性能证据

## 环境与方法

- 机器：Apple M4，10 physical/logical cores，Arm64。
- 系统：macOS 26.4.1。
- Runtime：.NET 10.0.2，Workstation Concurrent GC。
- 基线：`v0.6.6` (`9d560f4`)；候选：0.6.7 release candidate。
- 配置：Release、TCP、Balanced、连接池 1/1、默认 30 秒 request timeout、Add unary。
- 独立 worktree 构建基线；A/B 严格交替运行五次，每次 warmup 2 秒、采样 5 秒，取中位数。

## LoadTest 中位数

| 指标 | v0.6.6 | 0.6.7 RC | 变化 |
|---|---:|---:|---:|
| c1 QPS | 25,715.36 | 25,868.21 | +0.59% |
| c1 P99 | 72 us | 72 us | 0% |
| c128 QPS | 1,165,130.56 | 1,168,945.63 | +0.33% |
| c128 P99 | 214 us | 178 us | -16.82% |
| 调用错误 | 0 | 0 | 无变化 |

QPS、P99 与错误率通过 97%/105% 全局门禁。P99 收益来自本次样本的中位数，只作为本机同环境证据，不承诺所有部署都获得相同比例。

## Allocation

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add` 在 16 B 与 256 B 参数下，`v0.6.6` 和 0.6.7 RC 都是 672 B/op；每连接 pending 状态、monotonic deadline timer 和延迟服务端 Abandoned 跟踪没有增加同步 Unary 的托管分配。

## 失败实验与最终设计

首次实现为每个服务端调用立即租用 `ServerCallCancellationState` 并进入 striped cancellation map。一次拆分测量中：

- `v0.6.6` c128：1,138,683 QPS。
- 仅客户端 0.6.7 重构：1,171,833 QPS。
- 再加入无条件服务端状态跟踪：1,054,637 QPS。

因此无条件跟踪相对客户端候选回退约 10%，实现已撤销。最终采用延迟跟踪：

- 无 `CancellationToken` 且同步完成的服务不租对象、不进入 cancellation map。
- 支持协作取消的调用在执行前注册状态。
- 无 token 但真正异步未完成的调用在返回 read loop 前注册状态，以处理 deadline、Cancel 和迟到响应抑制。

这保留了 Abandoned 正确性语义，同时让同步 Unary 热路径避开额外锁和池操作。
