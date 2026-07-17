# SharpLink 0.6.9 性能与稳定性证据

## 环境与判定方法

- 机器：Apple M4，10 physical/logical cores，Arm64。
- 系统：macOS 26.4.1。
- Runtime：.NET 10.0.2，Workstation Concurrent GC。
- 配置：Release、TCP、Balanced、默认 30 秒 request timeout。
- Unary A/B：A 为 `8b1afc1`（P069-01），B 为 0.6.9 候选；独立 worktree 构建，严格 A/B 交替五轮，每轮 warmup 2 秒、采样 5 秒，取中位数。
- 门禁：QPS 不低于 A 的 97%，P99 不高于 A 的 105%，allocation 不高于已接受基线的 105%，调用错误不得增加。

## 五轮 Unary A/B

| 指标 | A | 0.6.9 候选 | 变化 |
|---|---:|---:|---:|
| c1 QPS | 25,722.15 | 26,200.65 | +1.86% |
| c1 P99 | 71 us | 71 us | 0% |
| c128 QPS | 1,244,470.89 | 1,242,651.98 | -0.15% |
| c128 P99 | 165 us | 162 us | -1.82% |
| 调用错误 | 0 | 0 | 无变化 |

所有指标通过全局回归门禁。本次改动主要修复生命周期、池化复用与停机正确性；这些差异没有达到可宣称性能收益的 3%/5% 阈值，因此不把本机波动写成性能提升。

## Streaming A/B

TCP、Balanced、连接池 1/1、server streaming、stream size 256、并发 32；严格交替五轮，每轮 warmup 2 秒、采样 5 秒：

| 指标 | A | 0.6.9 候选 | 变化 |
|---|---:|---:|---:|
| QPS | 9,619.31 | 9,479.52 | -1.45% |
| P99 | 3,973 us | 3,874 us | -2.49% |
| 调用错误 | 0 | 0 | 无变化 |

QPS/P99 均通过门禁。提前停止消费另由 1,500 次串行 early-break、64 路并发 pooled lease、10,000 次资源归零和 Chaos 混合负载验证，防止把“流能跑完”误当成“租约与 credit 一定正确归还”。

Release Gate 暴露异步 stream 注册的回池竞态后，最终修复只在每次 server/duplex stream 注册开始与结束各增加一次原子持有操作，不进入逐 item 热路径。相同 s2c/c32/256-item 场景复跑五次，中位数为 9,345.17 QPS、P99 4,008 us、零错误；相对上表候选分别为 -1.42% 与 +3.46%，仍在 97%/105% 门禁内。

## Allocation

BenchmarkDotNet `UnaryBenchmarks.Rpc_Add`：

| Payload | ShortRun | 固定 invocation job |
|---|---:|---:|
| 16 B | 672 B/op | 675 B/op |
| 256 B | 672 B/op | 674 B/op |

0.6.7/0.6.8 已接受基线为 672 B/op，0.6.9 ShortRun 保持不变。固定 invocation job 的 674–675 B/op 也低于 105% 上限。

## JIT 与 NativeAOT 矩阵 smoke

`eng/run-performance-matrix.sh` 统一输出带 commit、OS、架构、Runtime、GC、Transport、Profile、连接池、payload 和并发度的 JSON。当前 runner 完成 TCP/Balanced、pool 1/1 与 1/4、payload 0/256 B/64 KiB、c1/c32/c128、Unary/OneWay/`Task.Yield()`/1 ms async/server streaming 的 JIT 与 NativeAOT smoke：

- JIT 16 份报告；除单独标记的 backpressure 注入外，正常矩阵调用错误为 0。
- NativeAOT 14 份报告；调用错误为 0，publish 为 0 个 trimming/AOT 警告。
- NativeAOT 的报告 JSON 使用 source-generated `JsonSerializerContext`，没有反射序列化 fallback。

pool 1/1、c128 的代表性结果如下；短 smoke 用于检测灾难性回退，不用单次结果宣称 JIT/AOT 孰优：

| 场景 | JIT QPS / P99 | NativeAOT QPS / P99 |
|---|---:|---:|
| Empty Unary | 1,231,452.76 / 197 us | 1,145,576.10 / 207 us |
| 64 KiB Unary | 26,439.59 / 6,509 us | 27,225.53 / 7,752 us |
| `Task.Yield()` | 474,853.27 / 719 us | 444,908.63 / 637 us |
| 1 ms async | 80,596.32 / 2,874 us | 70,860.51 / 2,999 us |
| Server stream | 11,028.35 / 13,188 us | 10,673.67 / 13,220 us |

OneWay 的成功含义是“本地有界 SendPump 接受”，不含服务端 ACK。单生产者 JIT/NativeAOT 均为零错误；多生产者无节流测试会按设计耗尽有界发送队列，因此单独输出 `oneway-backpressure`，其 `ResourceExhausted` 不混入正常成功率。完整 Transport/Profile/payload/c1/c8/c32/c128/JIT/AOT 矩阵由同一脚本的 `SHARPLINK_MATRIX_TIER=full` 模式执行；正式性能 runner 必须使用默认 5 秒 warmup、20 秒采样，不能用 smoke 数据更新基线。

## Chaos 与 retained memory

本机两分钟混合 Chaos 结果：

- 2,938,187 次成功调用。
- 3,150,125 次预期故障注入结果。
- 9 次滚动重启，最慢一次从停服到连续五次成功探针 RPC 为 11.040 秒。
- 0 次非预期失败。
- 结束时 connections、calls、pending requests、streams、send queue bytes 全部为 0。

Chaos 的故障归因使用奇偶代次：调用只在开始于故障代次或跨越代次边界时被计为预期失败；新 listener 启动后必须以 20 ms 间隔连续完成五次真实探针 RPC 才结束故障代次，任一失败会清零连续计数。旧的固定 8 秒窗口和单次成功判定均已删除：前者会在慢 runner 尚未恢复时制造假阳性，后者会把瞬时连通误报为稳定恢复。修复前 30 秒复现为 7 次 `Stream dispatcher has no codec`；注册持有权修复后相同参数为 0 次非预期失败。

短生命周期 reconnect worker 在 Linux Release Gate 中曾连续恢复三个代次，但第四个代次在完整 30 秒预算内没有完成探针。最终实现改为每 Client 一个由容量 1 semaphore 驱动、随 Client Stop 取消和等待的持久 supervisor；它在被唤醒后按实际 Ready 数补连接，不再依赖 worker 退出与新断连信号之间的窄竞态。该改动只进入断线冷路径，不改变正常 RPC 热路径。

恢复硬上限为 30 秒，来源是 Server 最多 5 秒强制清理、10 秒 handshake、抖动后最多 6 秒重连退避及 2 秒调用 timeout 的组合预算与调度余量，而非任意时间窗口。任何已经开始的重启都使用独立恢复预算；即使负载总时长先结束，也必须完成成功探针或明确失败。

两分钟样本包含启动及对象池预热，retained memory 增长不参与六小时门禁。持续至少六小时后才比较最后六小时窗口；正式 24 小时连续长稳命令与判定规则见 `doc/chaos-0.6.9.md`，不能把多个短 CI job 拼接成连续长稳证据。

## 0.6.x 收敛轨迹

下表只用于定位趋势。各版本的同版本 A/B 才是有效门禁；跨版本样本的采样时长和当时 runner 负载不完全一致，不能直接计算总体收益。

| 版本 | c128 Unary 线索 | Allocation | 主要结论 |
|---|---:|---:|---|
| 0.6.0 | 约 1.08M QPS / P99 467 us | 832 B/op | 初始审计线索，不是正式基线 |
| 0.6.6 | 1.144M QPS / P99 244 us | 800–801 B/op | 删除已证实的无效锁、重复解析和 Scope 分配 |
| 0.6.7 | 1.169M QPS / P99 178 us | 672 B/op | 每连接 pending/deadline 状态统一，timeout 完成路径锁消失 |
| 0.6.8 | 1.062M QPS / P99 374 us | 672 B/op | 该项为较短的 server observer 专项样本；同步/异步路径均过同版本 A/B |
| 0.6.9 | 1.243M QPS / P99 162 us | 672 B/op | 有界停机、dispatcher 池上限和 teardown/credit 竞态收敛 |

## 调查后不修改的候选

以下项目没有达到证据阈值，0.6.9 不进行“凭观感”的重构：

- `StreamFlowController` 保留单 gate：micro 为 0 B/op，没有 profile 证明锁等待超过目标场景 CPU 的 2%。
- Writer Pool 保留 `ConcurrentQueue`：缺少 `ConcurrentStack` 达到 3%/5% 收益且 RSS 不增长的证据。
- Server Interceptor pipeline 不池化：默认无 interceptor 已直达 Stub，启用场景尚未证明 delegate/pipeline 是主要分配源。
- Throughput 1 ms flush 保留现有计时模型：linked CTS 没进入主要 allocation profile，改为复用 timer 会增加 wake-up 竞态。
- Generated Stub 不跨 Context 缓存 provider lookup：primitive/DTO 已有静态或构造期缓存，剩余 lookup 没达到阈值，跨 Context 缓存反而破坏 Codec 隔离。
- 单连接选择、ready snapshot 和无遥测 listener 快速路径已经无锁/无额外分配，不重复重写。

Protocol v2 wire format 在 0.6.9 没有改变；本轮只加强本地边界、生命周期、内存保留与验证工具。
