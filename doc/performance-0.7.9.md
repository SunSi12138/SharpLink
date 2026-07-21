# SharpLink 0.7.9 本地性能与组合 smoke

本记录是本地开发证据，不替代固定硬件、跨平台与 release soak。

## endpoint 稳态矩阵

本地 JIT smoke 以真实 TCP 服务运行两套 16-case 矩阵，warmup 0 秒、采样 1 秒：

- static：1/2 endpoint、P2C/LeastPending、并发 1/8、payload 0/256 B；16/16 通过、零失败，原始 JSON 在 `artifacts/performance/v0.7.9/static/`。
- dynamic Delegate Resolver：同一矩阵；16/16 通过、零失败，原始 JSON 在 `artifacts/performance/v0.7.9/dynamic/`。

代表性 endpoint=2、P2C、并发 8 的结果为：0 B **138,682 QPS / P99 84 µs**，256 B **131,127 QPS / P99 87 µs**。这些短窗口用于确认 0.7.7–0.7.9 的 Retry/admission/breaker 默认关闭时，static/dynamic 常规路径没有失败或明显的延迟异常；正式 release 应以五轮交替基准和 full matrix 复核。

## 默认路径与 enabled 路径

未配置 Retry/Admission/Breaker 时，fixed single endpoint 不创建 resolver、candidate、retry state、admission token 或 breaker state。Retry 仅在显式标记 `[Idempotent]` Unary 且 Builder 启用后创建 attempt state；admission/breaker 仅在 endpoint topology 且显式启用时创建 policy state。

`SharpLink.AotSmoke` 已以 `osx-arm64` NativeAOT 发布并运行 TCP 路径，输出 `AOT_SMOKE_PASS transport=tcp`。PackageSmoke 从本地生成的 0.7.9 packages 还原并运行通过。
