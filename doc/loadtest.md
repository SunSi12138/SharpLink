# LoadTest 使用说明

项目：`test/SharpLink.LoadTest`

## 目标

- 支持同机/异机压测：`local | server | client`
- 支持多传输层：`tcp | uds | namedpipe | anonymous`
- 支持多并发阶梯压测与 Prometheus 指标导出
- 输出更完整的统计：`qps / 错误率 / p50 p95 p99 p99.9 / avg / min / max`

## 构建

```bash
dotnet build test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -v minimal
```

## 快速开始

1. TCP（同机）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode local \
  --transport tcp \
  --concurrency 1,2,4,8,16,32,64 \
  --warmup 5 \
  --duration 20
```

2. UDS（同机）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode local \
  --transport uds \
  --concurrency 1,2,4,8,16,32,64 \
  --warmup 5 \
  --duration 20
```

3. 命名管道（同机）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode local \
  --transport namedpipe \
  --concurrency 1,2,4,8,16,32,64 \
  --warmup 5 \
  --duration 20
```

4. 匿名管道（同机，仅 local）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode local \
  --transport anonymous \
  --concurrency 1,2,4,8,16,32,64 \
  --warmup 5 \
  --duration 20
```

## 异机模式

1. 机器 A（服务端）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode server \
  --transport tcp \
  --bind-ip 0.0.0.0 \
  --port 19100
```

2. 机器 B（客户端）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode client \
  --transport tcp \
  --host <server-ip> \
  --port 19100 \
  --concurrency 1,2,4,8,16,32,64 \
  --warmup 5 \
  --duration 20
```

说明：
- 异机通常只建议 `tcp`
- `anonymous` 仅支持 `--mode local`

## 参数

- `--mode`: `local | server | client`
- `--transport`: `tcp | uds | namedpipe | anonymous`
- `--host`: client 目标地址（默认 `127.0.0.1`）
- `--bind-ip`: server 监听地址（默认 `0.0.0.0`）
- `--port`: TCP 端口（默认 `19100`）
- `--operation`: `add | echo`（默认 `add`）
- `--payload-size`: `echo` 负载长度（默认 `64`）
- `--concurrency`: 并发列表（逗号分隔，默认 `1,2,4,8,16,32`）
- `--warmup`: 每个并发档预热秒数（默认 `5`）
- `--duration`: 每个并发档正式压测秒数（默认 `20`）
- `--metrics-port`: Prometheus 端口（`<=0` 关闭，默认 `9464`）
- `--heartbeat-interval`: client 心跳间隔秒（默认 `10`）
- `--heartbeat-check-interval`: server 心跳检查间隔秒（默认 `10`）
- `--heartbeat-timeout`: 心跳超时秒（默认 `120`）
- `--help`: 显示帮助

## 固定传输标识（无需传参）

- UDS 路径：
1. Windows: `%TEMP%/sl_lt.sock`
2. 类 Unix: `/tmp/sharplink-loadtest.sock`

- 命名管道名：`sharplink-loadtest`

## 输出字段

每个并发档会输出：

- `qps`: 实际耗时计算的吞吐
- `ok / fail`: 成功/失败总数
- `err`: 错误率
- `p50 / p95 / p99 / p99.9`: 延迟分位（微秒）
- `avg / min / max`: 平均/最小/最大延迟（微秒）
- `dur`: 当前档实际运行时长（秒）
- `Failures`: TopN 异常类型统计（如 `TimeoutException:12`）

## Prometheus 指标

- `sharplink_load_test_total_success`
- `sharplink_load_test_total_failure`
- `sharplink_load_test_stage_qps{concurrency,operation}`
- `sharplink_load_test_stage_error_rate_percent{concurrency,operation}`
- `sharplink_load_test_stage_latency_us{concurrency,operation,quantile}`
- `sharplink_load_test_realtime_qps{concurrency,operation}`
- `sharplink_load_test_realtime_latency_us{concurrency,operation,quantile}`

其中 `quantile` 包含：`0.50 / 0.95 / 0.99 / 0.999 / avg`（stage）和 `0.50 / 0.95 / 0.99 / 0.999`（realtime）。

## 建议的压测流程

1. 固定机器电源策略（高性能）与 CPU 频率策略
2. 关闭无关后台任务，避免抖动
3. 每组配置至少跑 3 次，取中位数
4. 先跑 `add` 基线，再跑 `echo + payload-size`
5. 同机先比较不同 transport，再做异机 TCP
