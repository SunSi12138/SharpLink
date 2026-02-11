# Load Test 使用说明

项目：`test/SharpLink.LoadTest`

目标：

- QPS 压测（包含单线程和多并发）
- 支持同机模式（server+client）
- 支持异机模式（server/client 分离）
- 提供 Prometheus 指标输出，可接入 Grafana

## 构建

```bash
dotnet build test/SharpLink.LoadTest/SharpLink.LoadTest.csproj -v minimal
```

## 运行模式

## 1. 同机模式（默认）

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode local \
  --port 19100 \
  --operation add \
  --concurrency 1,2,4,8,16,32,64 \
  --warmup 5 \
  --duration 20 \
  --metrics-port 9464
```

## 2. 异机模式

机器 A（服务端）：

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode server \
  --bind-ip 0.0.0.0 \
  --port 19100 \
  --heartbeat-timeout 120
```

机器 B（客户端）：

```bash
dotnet run -c Release --project test/SharpLink.LoadTest -- \
  --mode client \
  --host <server-ip> \
  --port 19100 \
  --operation add \
  --concurrency 1,2,4,8,16,32,64 \
  --warmup 5 \
  --duration 20 \
  --metrics-port 9464
```

## 参数

- `--mode`：`local | server | client`
- `--host`：client 模式目标地址（默认 `127.0.0.1`）
- `--bind-ip`：server 监听地址（默认 `0.0.0.0`）
- `--port`：端口（默认 `19100`）
- `--operation`：`add | echo`（默认 `add`）
- `--payload-size`：`echo` 负载长度（默认 `64`）
- `--concurrency`：并发列表，逗号分隔（默认 `1,2,4,8,16,32`）
- `--warmup`：预热秒数（默认 `5`）
- `--duration`：每个并发档测试秒数（默认 `20`）
- `--metrics-port`：Prometheus 指标端口，`<=0` 关闭（默认 `9464`）
- `--heartbeat-interval`：client 心跳发送间隔秒（默认 `10`）
- `--heartbeat-check-interval`：server 心跳检查间隔秒（默认 `10`）
- `--heartbeat-timeout`：超时秒（默认 `120`）

## 输出说明

每个并发档会输出：

- `qps`
- `ok/fail`
- `p50/p95/p99`（微秒）

同时暴露 Prometheus 指标：

- `sharplink_load_test_total_success`
- `sharplink_load_test_total_failure`
- `sharplink_load_test_realtime_qps{concurrency,operation}`
- `sharplink_load_test_realtime_latency_us{concurrency,operation,quantile}`
- `sharplink_load_test_stage_qps{concurrency,operation}`
- `sharplink_load_test_stage_latency_us{concurrency,operation,quantile}`

## Grafana 接入

1. 在 Prometheus `scrape_configs` 加入：

```yaml
scrape_configs:
  - job_name: 'sharplink-loadtest'
    static_configs:
      - targets: ['<client-host>:9464']
```

2. Grafana 添加 Prometheus 数据源后，使用以下查询示例：

- 实时 QPS：`sharplink_load_test_realtime_qps`
- 实时 P99：`sharplink_load_test_realtime_latency_us{quantile="0.99"}`
- 阶段汇总 QPS：`sharplink_load_test_stage_qps`
- 总失败：`sharplink_load_test_total_failure`

## 注意

- 若要做“稳定基线”，建议固定 CPU 频率、关闭其他高负载任务，并多次运行取中位数。
- 异机压测时请确保网络链路稳定（尤其是 Wi-Fi 场景）。
