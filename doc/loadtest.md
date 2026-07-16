# LoadTest 使用说明

本仓库当前有两套压测程序：

- `test/SharpLink.LoadTest`：一元 RPC 压测（`add/echo`）
- `test/SharpLink.StreamLoadTest`：流式 RPC 压测（`unary/c2s/s2c/duplex`）

公共运行时与传输封装已下沉到：

- `test/SharpLink.LoadTestBase`

## 目标

- 支持 `local | server | client`
- 支持 `tcp | uds | namedpipe | anonymous`
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

3. 命名管道（本机）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- --mode local --transport namedpipe
dotnet run -c Release --project test/SharpLink.StreamLoadTest -- --mode local --transport namedpipe
```

4. UDS（本机）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- --mode local --transport uds
dotnet run -c Release --project test/SharpLink.StreamLoadTest -- --mode local --transport uds
```

5. 匿名管道（仅 `--mode local`）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- --mode local --transport anonymous
dotnet run -c Release --project test/SharpLink.StreamLoadTest -- --mode local --transport anonymous
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
- `--transport`: `tcp | uds | namedpipe | anonymous`
- `--host`: client 目标地址（默认 `127.0.0.1`）
- `--bind-ip`: server 监听地址（默认 `0.0.0.0`）
- `--port`: TCP 端口（`LoadTest=19100`，`StreamLoadTest=19150`）
- `--uds-path`: UDS 路径（可覆盖默认值）
- `--pipe-name`: 命名管道名（可覆盖默认值）
- `--concurrency`: 并发列表，逗号分隔
- `--warmup`: 预热秒数
- `--duration`: 正式压测秒数
- `--heartbeat-interval`: client 心跳间隔秒
- `--heartbeat-check-interval`: server 心跳检查间隔秒
- `--heartbeat-timeout`: 心跳超时秒
- `--min-connections`: Client 初始连接数（默认 `1`）
- `--max-connections`: Client 压力扩容上限（默认 `1`，范围 `1..64`）

`anonymous` 传输的 `--max-connections` 必须为 `1`。验证连接池时可在 TCP/UDS/NamedPipe 模式增加 `--min-connections 1 --max-connections 4`；最终实际连接数仍由并发压力触发，不会按每次 RPC 新建连接。

LoadTest 专有：

- `--operation`: `add | echo`（默认 `add`）
- `--payload-size`: `echo` 字符串长度（默认 `64`）
- `--metrics-port`: Prometheus 端口（`<=0` 关闭，默认 `9464`）

StreamLoadTest 专有：

- `--operation`: `all | unary | c2s | s2c | duplex`（默认 `all`）
- `--stream-size`: 单次流调用的元素数量（默认 `256`）

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
- `ok / fail`
- `err`
- `p50 / p95 / p99`
- `avg / max`
- `dur`
- `Failures`（异常 TopN）

`LoadTest` 额外输出：

- `p99.9`
- `min`
- Prometheus 实时指标

## Prometheus 指标（LoadTest）

- `sharplink_load_test_total_success`
- `sharplink_load_test_total_failure`
- `sharplink_load_test_stage_qps{concurrency,operation}`
- `sharplink_load_test_stage_error_rate_percent{concurrency,operation}`
- `sharplink_load_test_stage_latency_us{concurrency,operation,quantile}`
- `sharplink_load_test_realtime_qps{concurrency,operation}`
- `sharplink_load_test_realtime_latency_us{concurrency,operation,quantile}`
