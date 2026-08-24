# SharpLink 文档

本文档集以当前 `2.0.0` 源码为准，不再按旧开发小版本保存用户文档。公开 API 的精确参数、返回值和异常语义同时通过 NuGet 包内 XML 文档提供。

## 学习路径

1. [快速开始](getting-started.md)：包引用、契约、服务、Client/Server 生命周期。
2. [契约与序列化](contracts-and-codecs.md)：五类 RPC、DTO 规则、原生 Codec、SharpPack 与 Adapter。
3. [调用、流式与取消](calls-and-streaming.md)：deadline、metadata、背压、OneWay 和取消契约。
4. [传输与部署](transports.md)：TCP/TLS、UDS、NamedPipe、AnonymousPipe、SharedMemory 与 NativeAOT。
5. [安全](security.md)：认证、授权、TLS、原始结构体序列化边界和错误信息边界。
6. [服务发现与韧性](resilience.md)：连接池、静态/动态端点、负载均衡、Retry、Circuit Breaker。
7. [服务端接入控制](admission-control.md)：并发、速率、排队和分区限制。
8. [Hosting 与服务生命周期](hosting-and-services.md)：Generic Host、DI、健康检查、排空与动态模块。
9. [拦截器与可观测性](observability.md)：Interceptor、Activity、Meter 与日志事件。
10. [多集群与动态模块](dynamic-modules-and-multicluster.md)：编译期路由、动态注册/替换/注销和 ALC 所有权。
11. [限制与调优](limits-and-tuning.md)：默认值、硬上限和性能 Profile。
12. [故障排查](troubleshooting.md)：常见配置、协议、资源和生命周期错误。
13. [迁移到 2.0](migration.md)：Generated ABI（API 5）、包依赖变化和完整重建要求。

深入资料：[架构](architecture.md)、[构建计划与 Builder 单次使用](runtime-phase-11-build-plan.md)、[Protocol v2](protocol-v2.md)、[UnsafeBlit 兼容性](codec-compatibility.md)、[UnsafeBlit padding 安全评估](unsafe-blit-padding-security.md)、[负载工具](loadtest.md)、[性能基线](performance.md)、[发布流程](releasing.md)。

## 特性与可运行证据

| 能力 | 文档 | Demo |
|---|---|---|
| 基本 Unary、DTO、SharpPack | 快速开始、契约与序列化 | `demo/HelloWorld` |
| 五类调用与背压 | 调用、流式与取消 | `demo/Streaming`、`demo/Oneway` |
| 取消与 deadline | 调用、流式与取消 | `demo/Cancel`、`demo/Timeout` |
| Generic Host、健康检查 | Hosting 与服务生命周期 | `demo/HostApplication` |
| 结构化日志 | 拦截器与可观测性 | `demo/Log` |
| 认证、身份、scope/tenant | 安全 | `demo/Security` |
| 协商压缩 | 契约与序列化、限制与调优 | `demo/Compression` |
| 并发接入控制 | 服务端接入控制 | `demo/AdmissionControl` |
| Interceptor 与 ActivitySource | 拦截器与可观测性 | `demo/InterceptorsTelemetry` |
| 静态端点、负载均衡、Retry、Breaker | 服务发现与韧性 | `demo/Resilience` |
| 五种内置传输 | 传输与部署 | `demo/TransportMatrix` |
| 编译期多集群路由 | 多集群与动态模块 | `demo/MultiCluster` |
| 分离契约/服务/客户端部署 | 快速开始 | `demo/SeparatedContracts`、`SeparatedServer`、`SeparatedClient` |

动态程序集需要独立可卸载 `AssemblyLoadContext` 和外部插件文件，无法在单文件入门 Demo 中真实证明卸载。该能力由 `SharpLink.DynamicContracts`、`SharpLink.DynamicServices` 与 IntegrationTests 的注册、替换、排空、回滚和 collectible ALC 场景验证。

## 文档接受标准

- `src/` 所有公开 API 在开启 XML 文档时以 CS1591 为错误，缺口必须为 0。
- 每个发布 NuGet 包必须包含与主程序集同名的 XML 文件。
- 所有 Demo 必须在 Release 下构建并运行成功。
- 文档链接、命令、默认值和限制必须可由当前代码或自动化测试验证。
- 性能数字只在固定环境、精确提交和明确负载下发布，不把历史开发机结果当作当前版本承诺。
- [Runtime interceptor replacement](runtime-interceptors.md)
