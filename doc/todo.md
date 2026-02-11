# SharpLink 待办与改进方向

本文档按优先级列出当前项目仍存在的问题与建议改进路径。

## P0（优先处理）

### 1. 断连与挂起请求收敛一致性

现状：
- 连接断开时，仍可能存在 pending 请求/流等待完成（历史上出现过 benchmark 卡住）
- Client/Server 的异常退出路径较多，收敛逻辑分散

建议：
- 统一断连收敛入口：`FailAllPendingRequests + CompleteAllStreams`
- 为断连增加一致错误码/异常类型，避免字符串比较
- 增加回归测试：网络断开、中途 server dispose、心跳超时

### 2. 生命周期与后台任务管理

现状：
- 存在 `Task.Run` 启动的后台循环，缺少统一的停止/等待策略
- demo/benchmark 场景容易出现“后台仍在 build/run”的体验问题

建议：
- 所有后台任务都记录并在 `Dispose/Stop` 等待完成
- 引入“幂等停止”状态机，避免重复 dispose 引发竞态

### 3. 协议错误模型过于字符串化

现状：
- 多处以字符串返回错误（`RpcResponse + IsError + message`）

建议：
- 定义标准错误结构（错误码、消息、可选详情）
- 约定取消、超时、服务不存在、反序列化失败等错误码

## P1（短期优化）

### 4. 可观测性与日志体系

现状：
- 日志机制比较简单

建议：
- 增加关键维度：`requestId/interfaceHash/methodHash/streamId/elapsed`
- 引入可选事件源或 metrics 钩子

### 5. 安全与握手机制

现状：
- 当前握手示例中存在固定字符串认证逻辑（演示性质）

建议：
- 抽象 `IAuthenticator`（server/client）
- 支持 token/签名/时间窗等策略
- 预留后续 TLS 或外部通道安全接入能力

### 6. 传输层配置一致性

现状：
- TCP/UDS/NamedPipe 配置能力存在但分散

建议：
- 统一 `TransportOptions`
- 明确平台兼容矩阵（Windows/Linux/macOS）
- 提供连接超时、重试、keepalive、buffer 策略

### 10. 测试体系完善（单元测试优先）

现状：
- 已有 Integration/AOT/Load/Benchmark 与初始 `SharpLink.UnitTests`（TUnit）。
- 关键模块单测覆盖仍偏低，边界场景（并发、取消、断连收敛、池化阈值）不足。

建议：
- 统一以 `SharpLink.UnitTests` 承载单元测试，保持 `dotnet test` 一致入口。
- 优先补齐模块：`BufferWriterPool`、`StreamManager`、`RequestManager`、`PacketHelper`、`Builder/Options`。
- 为高风险路径补回归用例：半包/坏包、取消竞态、多流并发结束、断连后 pending 收敛。
- CI 分层执行：`Unit (fast)`、`Integration (medium)`、`Load smoke (slow)`，并对失败率设置门槛。

## P2（中期演进）

### 7. 协议版本与能力协商

目标：
- 支持协议向后兼容演进
- 连接建立阶段协商能力（stream/cancel/压缩等）

### 8. 生成器工程化

目标：
- 增加更细粒度诊断与 code-fix 指引
- 生成代码加注释开关（调试友好）
- 提供生成统计/trace 便于定位问题

### 9. 性能体系完善

目标：
- 基准结果固化为基线（可对比 PR 回归）
- 增加高并发、长连接、混合负载（unary + stream）压测
- 引入 CI 自动化 benchmark smoke

## 文档与示例待办

- 统一示例命名（如 `OnwWay` 目录命名规范化）
- 补充“从零到一”教程（定义接口、生成代码、部署 server/client）
- 补充“生产化清单”（日志、监控、限流、超时、重试、安全）
