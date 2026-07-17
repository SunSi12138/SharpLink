# SharpLink 0.6.6 性能证据

## 环境与方法

- 机器：Apple M4，10 physical/logical cores，Arm64。
- 系统：macOS 26.4.1。
- Runtime：.NET 10.0.2，Workstation Concurrent GC。
- 基线：`5dd7b89`；候选：`bda8330`。
- 配置：Release、TCP、Balanced、连接池 1/1、默认 request timeout、Add unary。
- Load A/B 交替执行，各五次；每次 warmup 2 秒、采样 5 秒，中位数作为 0.6.6 release smoke 结论。

## LoadTest 中位数

| 指标 | 5dd7b89 | bda8330 | 变化 |
|---|---:|---:|---:|
| c1 QPS | 25,159.73 | 25,482.87 | +1.28% |
| c128 QPS | 1,134,650.24 | 1,143,989.83 | +0.82% |
| c128 P99 | 258 µs | 244 µs | -5.43% |
| 调用错误 | 0 | 0 | 无变化 |

短时样本包含偶发系统调度离群值，因此只使用五次中位数，不使用最好单次结果。QPS、P99 和错误率均通过 97%/105% 全局回归门禁。

## Micro Benchmark

| 场景 | 基线 | 0.6.6 候选 | 结论 |
|---|---:|---:|---|
| CallContext Push/Dispose | 约 21.7–22.1 ns；104 B/op | 约 18.5–18.8 ns；72 B/op | 时间约 -14%，分配 -30.8% |
| Contiguous Request + Metadata parser | 102.39 ns；312 B/op | 24.24 ns；0 B/op | 重复 Metadata 解码从 parser 消失 |
| 1-byte segmented Metadata parser | 1.654 µs；3000 B/op | 265.46 ns；0 B/op | 多段路径不再构造 Metadata/string |
| Rpc_Add | 已接受线索 832 B/op | 800–801 B/op | 分配未回退 |

## 保留与撤销结论

- 保留 SendPump waiter-null fast path：没有 waiter 时不再进入 admission lock，锁内仍保留二次检查以防 lost wake-up。
- 保留单连接池 expansion fast path：`ReadyConnectionCount >= MaxConnections` 时不进入 `_stateGate`，锁内仍保留二次检查。
- 保留 Generated Stub stack scratch：生成代码中每个多段固定宽度参数的 `new byte[]` 已消失；固定内建字段大小远低于 1 KiB 阈值。
- 保留 CallContext struct scope 与 parser 单次语义解码：两项均明显超过证据门槛。
- 本版本没有改写 Stream Dispatcher SPSC、StreamFlowController、BufferWriterPool 容器或 Interceptor pool；这些候选留待专项 profile 达到路线图阈值后再处理。
