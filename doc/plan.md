# SharpLink 计划

## 目标

- 形成稳定的分层架构：`Abstractions / Runtime / Sdk / Client / Server / Hosting / Generator`
- 提供可直接落地的开发体验：仅依赖 `Sdk` 定义服务契约，按需引入 `Client` 或 `Server`
- 在流式和取消场景下保证协议闭环与行为一致性
- 提供可持续的性能基线与回归验证体系

## 当前阶段（已完成）

- Client/Server 共享能力下沉到 Runtime/Abstractions
- 生成器支持：
  - `[Oneway]`
  - `Task/ValueTask/IAsyncEnumerable` 返回约束诊断（`SHARPLINK001`）
  - 多调用入口生成（无 payload、流式、oneway）
- 协议新增 `Cancel` 包，支持请求级取消链路
- 增加多套 Demo（HelloWorld/Streaming/Host/Cancel/Oneway）
- 基准项目重写为 Unary/Streaming 两类场景

## 下一阶段（1-2 个迭代）

1. 稳定性收敛
- 断连后的 pending 请求与流统一 fail-fast
- 心跳、取消、超时在客户端/服务端行为一致
- 连接关闭过程的资源释放与异常边界统一

2. 传输层能力增强
- 统一 transport 配置模型（超时、buffer、backlog、keepalive）
- 明确 TCP/UDS/NamedPipe 的平台行为差异与约束
- 补齐断网/半开连接的自动恢复策略（可选）

3. 可观测性
- 增加请求 ID、方法哈希、耗时、异常类型等结构化日志
- 增加最小指标（QPS、P99、失败率）输出

## 中期方向

- 协议演进（版本协商、能力协商）
- Serializer/Transport 扩展点正式化
- 安全能力（认证、鉴权、可选加密）
- 文档和示例从“能跑”升级到“可生产参考”
