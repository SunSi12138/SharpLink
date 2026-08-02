# LoadTest 使用说明

本仓库当前有两套压测程序：

- `test/SharpLink.LoadTest`：一元 RPC 压测（`add/echo`）与长耗时调用容量测试（`hold`）
- `test/SharpLink.StreamLoadTest`：流式 RPC 压测（`unary/c2s/s2c/duplex/duplex-equivalent`）

公共运行时与传输封装已下沉到：

- `test/SharpLink.LoadTestBase`

## 目标

- 支持 `local | server | client`
- 支持 `tcp | uds | namedpipe | anonymous | sharedmemory`
- 支持并发阶梯压测与统计输出
- `LoadTest` 支持 Prometheus 指标导出

## 构建

```bash
dotnet build test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -v minimal
dotnet build test/SharpLink.StreamLoadTest/SharpLink.StreamLoadTest.csproj -v minimal
```

## 快速开始

1. LoadTest（本机 TCP）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode local \
  --transport tcp \
  --concurrency 1,2,4,8,16,32 \
  --warmup 5 \
  --duration 20
```

2. StreamLoadTest（本机 TCP，四种流场景）

```bash
dotnet run -c Release --project test/SharpLink.StreamLoadTest -- \
  --mode local \
  --transport tcp \
  --operation all \
  --concurrency 1,4,16 \
  --stream-size 512 \
  --warmup 5 \
  --duration 20
```

3. 长耗时 RPC 容量（本机 TCP）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode local \
  --transport tcp \
  --operation hold \
  --client-count 4 \
  --concurrency-per-client 2048 \
  --min-connections 1 \
  --max-connections 1 \
  --hold-duration 30 \
  --max-concurrent-calls-per-connection 2048 \
  --max-concurrent-calls-per-server 32768 \
  --max-pending-requests-per-connection 65536 \
  --request-timeout disabled \
  --metrics-port 0
```

`hold` 为每个客户端创建独立 `SharpLinkClient` 和独立连接池，并一次性发起指定数量的未完成调用。服务端使用 Singleton 探针和共享 gate：达到预计可接纳容量后统一计时释放，另有有界兜底释放，因而不需要在服务器已满时再发送一个会被拒绝的控制 RPC。

4. 命名管道（本机）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- --mode local --transport namedpipe
dotnet run -c Release --project test/SharpLink.StreamLoadTest -- --mode local --transport namedpipe
```

5. UDS（本机）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- --mode local --transport uds
dotnet run -c Release --project test/SharpLink.StreamLoadTest -- --mode local --transport uds
```

6. 匿名管道（仅 `--mode local`）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- --mode local --transport anonymous
dotnet run -c Release --project test/SharpLink.StreamLoadTest -- --mode local --transport anonymous
```

7. 共享内存（同机同用户）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode local --transport sharedmemory --profile throughput \
  --shm-name sharplink-loadtest --shm-capacity 33554432 --shm-spin-count 0
dotnet run -c Release --project test/SharpLink.StreamLoadTest -- \
  --mode local --transport sharedmemory --operation all
```

## 异机模式（TCP）

1. 机器 A（server）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode server \
  --transport tcp \
  --bind-ip 0.0.0.0 \
  --port 19100
```

2. 机器 B（client）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode client \
  --transport tcp \
  --host <server-ip> \
  --port 19100 \
  --concurrency 1,2,4,8,16,32 \
  --warmup 5 \
  --duration 20
```

说明：

- 异机通常使用 `tcp`
- `anonymous` 只支持 `--mode local`

## 参数

通用参数（两者都有）：

- `--mode`: `local | server | client`
- `--transport`: `tcp | uds | namedpipe | anonymous | sharedmemory`
- `--host`: client 目标地址（默认 `127.0.0.1`）
- `--bind-ip`: server 监听地址（默认 `0.0.0.0`）
- `--port`: TCP 端口（`LoadTest=19100`，`StreamLoadTest=19150`）
- `--uds-path`: UDS 路径（可覆盖默认值）
- `--pipe-name`: 命名管道名（可覆盖默认值）
- `--shm-name`: 共享内存逻辑端点名
- `--shm-capacity`: 可选的每方向容量（64 KiB–256 MiB、2 的幂）
- `--shm-spin-count`: 可选的本端自旋次数（0–4096）
- `--detailed-shm-evidence`: 启用共享内存热路径诊断计数；仅用于正确性与瓶颈定位，不得用于正式计时
- `--concurrency`: 并发列表，逗号分隔
- `--warmup`: 预热秒数
- `--duration`: 正式压测秒数
- `--heartbeat-interval`: client 心跳间隔秒
- `--heartbeat-check-interval`: server 心跳检查间隔秒
- `--heartbeat-timeout`: 心跳超时秒
- `--min-connections`: Client 初始连接数（默认 `1`）
- `--max-connections`: Client 压力扩容上限（默认 `1`，范围 `1..64`）
- `--max-send-queue-bytes`: 可选的有界 SendPump 容量覆盖；正式吞吐对比应让 Client 与 Server 使用同一个固定值
- `--max-concurrent-calls-per-connection`: 每条服务端物理连接的活跃调用上限（默认 1,024）
- `--max-concurrent-calls-per-server`: 单个服务端实例的全局活跃调用上限（默认 65,536）
- `--max-pending-requests-per-connection`: Client pending request 容量（默认 65,536，必须是 2 的幂）

`anonymous` 传输的 `--max-connections` 必须为 `1`。验证连接池时可在 TCP/UDS/NamedPipe/SharedMemory 模式增加 `--min-connections 1 --max-connections 4`；最终实际连接数仍由并发压力触发，不会按每次 RPC 新建连接。

LoadTest 专有：

- `--operation`: `add | echo | hold`（默认 `add`；另支持现有诊断操作 `empty/oneway/yield/delay`）
- `--client-count`: `hold` 的独立客户端数量（默认 4，其他操作忽略）
- `--concurrency-per-client`: `hold` 每个客户端一次性发起的调用数（默认 1,024）
- `--hold-duration`: `hold` 达到预计容量后的保持秒数（默认 30）
- `--payload-size`: `echo` 字符串长度（默认 `64`）
- `--compression`: `none | brotli`（默认 `none`）
- `--compression-level`: `fastest | optimal | smallest | nocompression`（默认 `fastest`，仅影响本地编码）
- `--compression-min-payload`、`--compression-min-savings-bytes`、`--compression-min-savings-ratio`: 压缩收益策略（默认 `1024 / 64 / 0.05`）
- `--payload-pattern`: `compressible | random`，随机输入使用固定 seed
- `--metrics-port`: Prometheus 端口（`<=0` 关闭，默认 `9464`）

StreamLoadTest 专有：

- `--operation`: `all | unary | c2s | s2c | duplex | duplex-equivalent`（默认 `all`；`all` 保留原有四种场景，不隐式加入等价验证负载）
- `--stream-size`: 单次流调用的元素数量（默认 `256`）
- `--message-bytes`: `duplex-equivalent` 的每条业务消息字节数（默认 `4096`）
- `--messages-per-stream`: `duplex-equivalent` 的每个已完成流双向消息数（默认 `8`）

`duplex-equivalent` 为跨框架比较提供严格 oracle：每个响应都校验 operation ID、顺序、长度和完整 payload，缺失、重复、错序、额外或损坏响应均计为 validation failure，阶段结束时的部分流取消单独计数且不进入成功吞吐。正式连接数对比必须固定 `--min-connections` 与 `--max-connections` 为同一个值；`1/64` 动态池不能替代 `1/1` 与 `64/64` 两条独立证据。

`hold` 同样要求 `--min-connections` 与 `--max-connections` 相等，并禁用 endpoint topology 与 admission，避免其他容量控制掩盖 call-capacity 结果。跨机运行时 Server 与 Client 应使用相同的调用上限参数；`hold` 不支持 anonymous pipe。

## 默认传输标识

- UDS 默认路径：
- `LoadTest`: `TransportDefaults.GetDefaultUdsPath("sharplink-loadtest")`
- `StreamLoadTest`: `TransportDefaults.GetDefaultUdsPath("sl_stream_loadtest")`
- 命名管道默认名：
- `LoadTest`: `sharplink-loadtest`
- `StreamLoadTest`: `sharplink-stream-loadtest`

## 输出说明

两者都会输出分阶段结果：

- `qps`
- `duplex-equivalent` 的 validated messages、messages/s 与每方向业务 MiB/s
- `ok / fail`
- `err`
- `p50 / p95 / p99`
- `avg / max`
- `dur`
- `echo` 的单向和请求+响应往返业务 payload MiB/s（不含 frame envelope）
- `Failures`（异常 TopN）
- JSON evidence 中的 CPU、allocated bytes、Gen0/1/2 GC
- SharedMemory 模式默认记录 negotiated capacity、notification backend、spill/wait/实际 notification 计数
- 使用 `--detailed-shm-evidence` 时，额外记录直接写入、spill 原因与复制、staging、通知请求/合并及游标刷新；这些高频观测会扰动热路径，只能作为诊断证据

`eng/run-performance-matrix.sh` 的 full tier 覆盖全部适用本机传输、三个 profile、payload `0/32/256/4096/65536/1048576`、连接池 `1/1` 与 `1/4`。小 payload 使用并发 `1/8/32/128/256/512`，64 KiB 使用 `1/8/32/128`，1 MiB 使用 `1/8/32`；显式设置 `SHARPLINK_MATRIX_CONCURRENCY` 时按调用者给定列表执行。正常吞吐场景固定 Client/Server send queue 为 64 MiB（可由 `SHARPLINK_MATRIX_MAX_SEND_QUEUE_BYTES` 覆盖），避免不同 profile 的队列容量成为吞吐混杂变量；`oneway-backpressure` 专项刻意保留 profile 默认队列并单独报告预期饱和。默认执行五轮，偶数轮反转传输顺序；原始 JSON 写入 `artifacts/performance/current/matrix`。SharedMemory 的正式基线必须同时列出同平台 TCP、UDS、NamedPipe 和 AnonymousPipe，不只选择有利对照。NativeAOT 独立进程 smoke 使用 `eng/run-shared-memory-aot-process-smoke.sh`。

静态 endpoint、动态 Resolver、admission、compression 和 interceptor 使用相应 LoadTest 参数或专项 Benchmark runner。最终性能结论只采用精确 RC 提交上同硬件、同配置、交替多轮的结果；短时 smoke 只证明矩阵可运行且无请求错误。

运行矩阵或 trace 前必须确认同机没有其他 LoadTest、StreamLoadTest、Chaos 或诊断采集进程。存在资源竞争时，整批吞吐、延迟、分配和 trace 均标记无效并从头重跑；错误数与资源归零仍可单独作为正确性线索，但不得转化为性能结论。

`LoadTest` 额外输出：

- `p99.9`
- `min`
- Prometheus 实时指标

`hold` 不以 QPS 为结论，输出：

- `client_count` / `connection_count`
- `attempted_calls` / `accepted_calls` / `peak_active_calls`
- `completed_calls` / `resource_exhausted_calls` / `resource_exhausted_reasons` / `cancelled_calls` / `other_failed_calls`
- `active_calls_after_release` / `healthy_calls_after_release`
- 每连接、每服务器和 pending request 上限
- `processor_count`、Runtime、GC、transport、profile 与失败摘要

正式容量结果至少覆盖低于、等于和高于服务器上限，随后验证 `active_calls_after_release: 0`、所有客户端健康调用成功，并在 TCP 之外再运行一种适用的本机传输。容量上限受可用内存、payload、Service scope 和运行时配置共同影响；出现操作系统内存压力的试次应作为上界失败点记录，不应宣称为稳定可用容量。

## Prometheus 指标（LoadTest）

- `sharplink_load_test_total_success`
- `sharplink_load_test_total_failure`
- `sharplink_load_test_stage_qps{concurrency,operation}`
- `sharplink_load_test_stage_error_rate_percent{concurrency,operation}`
- `sharplink_load_test_stage_latency_us{concurrency,operation,quantile}`
- `sharplink_load_test_realtime_qps{concurrency,operation}`
- `sharplink_load_test_realtime_latency_us{concurrency,operation,quantile}`
