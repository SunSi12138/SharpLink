# SharpLink 待办与改进方向

本文档记录当前主分支/开发分支共识之外，基于最近一次代码审计得到的真实问题与后续改进方向。

## 最近已处理（dev）

- AOT smoke 不再硬编码引用 `src/SharpLink.Generator/bin/Release/.../SharpLink.Generator.dll`，改为直接引用生成器项目作为 Analyzer，避免使用陈旧本地产物。
- `SharpClientBuilder` / `SharpLinkServerBuilder` 修复 `UseSerializer(...)` 仅在配置 `UseRpcSessionFlush(...)` 时才会生效的问题。
- `NamedPipeTransport` 在 Unix/macOS 上对过长 pipe name 做确定性缩短，避免触发底层 Unix Domain Socket 路径长度限制。
- `AotSmoke` 补齐复杂类型显式 `RpcCodecRegistry.Register(...)` 示例，当前冒烟程序已可运行通过。
- `one-way + client-stream` 调用现在会等待客户端流发送完成后再返回，消除集成测试里的累计值竞态。
- `AnonymousPipeTransport` 修复本机匿名连接在握手成功后过早释放 client handle 本地副本，导致后续 RPC 出现 `Operation canceled/Broken pipe` 的问题。
- `SharpLink.LoadTestBase` 修复匿名管道本机模式复用了错误的传输实例，`--transport anonymous` 本机压测现已可运行。
- `RpcSession.SendPump` 现在会将断管/对象释放视为 transport fault，匿名断连场景可以稳定 fail-fast，34 条集成测试已可正常结束。
- Runtime/Client 已引入 `SharpLinkException` / `SharpLinkErrorCode`，断连、远端错误、心跳超时、协议异常不再退化为普通 `Exception`。
- `ConnectOrThrowAsync()` 与 `ISharpLinkClientDiagnostics.LastConnectionException` 已补齐，握手拒绝原因现在可以结构化暴露给调用方和 Host 启动路径。
- 服务端 `UseAuthenticator(...)` 已支持 `SharpLinkAuthenticationResult`，可以显式返回认证失败码与消息，相关集成测试已覆盖。
- 服务方法执行期间已可通过 `SharpLinkCallContext.Current` 读取 `sessionId / subject / tenantId / scopes / expiresAt / claims`，认证上下文不再只停留在握手阶段。
- 服务端已新增 `SharpLinkAuthorization`，scope/tenant/expiry 失败会以原始错误码返回客户端，不再退化成普通 `RemoteError`。
- TCP 测试/冒烟链路已切到 `UseTcp(0)` + `ILocalEndPointTransport`，显著降低并发测试里的动态端口竞态。
- solution 中 `Oneway` 示例路径已与实际文件系统大小写对齐，降低 Linux/CI 环境下的构建风险。

## P0（优先处理）

### 1. 认证与安全模型仍需继续收口

现状：

- 当前已支持通过 Builder 配置客户端握手消息和服务端握手校验委托。
- 握手拒绝现在已有统一异常类型，服务端也可以返回明确拒绝错误码。
- 仍缺少身份上下文透传、签名/时间窗/TLS 等更完整的安全模型。

建议：

- 围绕现有 `UseAuthenticator(...)`、`SharpLinkAuthenticationResult`、`SharpLinkCallContext`、`SharpLinkAuthorization` 收敛默认用法，避免过早引入更重的策略框架
- 在文档中明确 `claims / tenant / scope / expiry` 的最小约定，降低接入方理解成本
- 继续补齐认证失败、过期、权限不足等场景的集成测试

### 2. 断连与挂起请求收敛一致性

现状：

- 连接断开时，pending 请求/流的主要 fail-fast 路径已经补齐，匿名管道断连回归也已覆盖。
- Client/StreamManager 现在会保留统一错误对象和错误码，测试已经覆盖断连/远端错误的结构化传播。
- `Client/Server/Runtime` 中断链路涉及 `RequestManager`、`StreamManager`、超时/取消回调，维护成本高。

建议：

- 统一断连收敛入口：`FailAllPendingRequests + CompleteAllStreams`
- 约定统一异常类型/错误码，避免字符串比较
- 补回归用例：网络断开、中途 `Dispose`、心跳超时

### 3. 生命周期与后台任务退出语义

现状：

- 这轮已收敛匿名管道导致的测试宿主取消问题，`SharpLink.IntegrationTests` 当前可稳定完成并退出。
- 后台循环与异步清理路径仍值得继续收紧，尤其是 `Dispose/Stop` 语义统一与更清晰的诊断边界。

建议：

- 收拢后台任务创建/停止策略，确保 `Dispose/Stop` 后可以稳定退出
- 对测试宿主与运行时退出路径增加诊断日志和最小复现用例

## P1（短期优化）

### 4. 序列化与 AOT 体验

现状：

- JIT 场景可以通过 `UseSerializer(MemoryPackCodec.Resolver)` 兜底复杂类型。
- NativeAOT 仍需要手工 `RpcCodecRegistry.Register(MemoryPackCodec<T>.Instance)`，门槛较高。

建议：

- 提供一组更明确的 AOT 注册辅助 API 或示例模板
- 在文档中列出“哪些类型必须显式注册”的最小规则
- 将 `AotSmoke` 纳入 CI 冒烟链路

### 5. 可观测性与日志体系

现状：

- 日志事件已经开始分化，但请求级上下文仍不完整。

建议：

- 增加 `requestId / interfaceHash / methodHash / streamId / elapsed`
- 引入可选 metrics 钩子或 `EventSource`

### 6. 传输层配置一致性

现状：

- `TCP / UDS / NamedPipe / AnonymousPipe` 能力齐全，但配置入口分散。
- Unix/macOS NamedPipe 兼容性问题已修复，仍需要对外说明平台差异。

建议：

- 统一 `TransportOptions`
- 在 README/文档中给出平台兼容矩阵与约束说明
- 明确 backlog、keepalive、connect timeout、flush 策略

### 7. 测试体系完善（单元测试优先）

现状：

- 已有 `Unit / Integration / Generator / AOT Smoke / Load / Benchmark` 测试层次。
- 关键边界仍有补测空间，例如半包/坏包、取消竞态、多流并发结束、断连后 pending 收敛。

建议：

- 继续扩展 `SharpLink.UnitTests`
- 将高风险路径补成回归用例，而不是仅依赖人工 smoke

## P2（中期演进）

### 8. 协议版本与能力协商

目标：

- 支持协议向后兼容演进
- 连接建立阶段协商能力（stream / cancel / compression / serializer capability）

### 9. 生成器工程化

目标：

- 增加更细粒度诊断与 code-fix 指引
- 暴露更清晰的生成统计与 trace
- 降低生成代码与运行时接口演进时的脱节风险

### 10. 性能治理体系

目标：

- 基准结果固化为基线，可用于 PR 回归比较
- 增加高并发、长连接、混合负载（unary + stream）压测
- 重新整理 `doc/performance.md`，避免留下半成品文档
