# 故障排查

## Build 失败

- `SHARPLINKxxx`：先修契约/DTO/服务签名；不要隐藏 Generator diagnostics。
- CS1591：发布源码公开 API 缺 XML 文档；测试和 Demo 不在该 gate。
- 生成类型找不到 Abstractions：确认契约项目引用 2.0 SDK，且没有排除其 Abstractions 依赖；参考 `SeparatedContracts`。API 5 生成程序集不应引用 Runtime。
- 业务源码直接使用 Runtime 类型但找不到程序集：显式引用 `SharpLink.Runtime` 或相应 Client/Server 应用包；SDK 2.0 不再传递引入 Runtime。
- Manifest 显示 incoming API 3 或 API 4、required API 5：进程正在加载 1.1.x 或 2.0 开发期生成程序集。统一全部 SharpLink 包为 2.0，删除所有契约、服务和插件项目的 `bin/obj` 后重新构建；2.0 不提供任何旧 Generated ABI 兼容开关。
- Manifest 版本或 schema 冲突：确认 Client/Server/SDK/Generator 包版本一致，并清理旧 `bin/obj` 后重建。

## Client 无法 Ready

- 确认 transport 地址、TLS SNI/证书、TLS timeout、RPC handshake timeout和认证 provider。
- 静态 cluster 的 `MinReadyEndpoints` 不能超过 endpoint/connection budget。
- dynamic resolver 空 snapshot、重复 id 或超出 `MaxEndpoints` 会被拒绝，last-good 继续使用。
- 观察 `SharpLink.Client` Activity、connection/resolver/auth 指标和结构化日志 event id。

## 调用失败 code

- `InvalidArgument`：本地或业务参数不合法。
- `DeadlineExceeded`：最早 deadline 到期，包括排队时间。
- `Cancelled`：调用方 token 或消费方放弃。
- `ResourceExhausted`：send queue、pending、stream、call、admission 或其他有界资源耗尽。
- `Unavailable`/`ConnectionClosed`：endpoint/连接不可用；只有 Idempotent Unary 可能 retry。
- `ProtocolViolation`：peer frame/handshake 违反协议。
- `DataLoss`：业务 payload 编码、nullability、UTF-8、length 或 Codec 完整消费失败。
- `Internal`：未映射业务异常或框架安全边界；查 Server 日志，不向对端暴露敏感详情。

## Streaming 卡住或内存增长

- 确认消费者持续枚举并最终 Dispose；不要创建后遗忘返回流。
- 检查 stream/connection receive window 和 send queue 指标。
- 不要让服务生成速度长期超过消费者；框架背压不是无限队列。
- 自定义 Codec/provider 不得保留输入或 writer。
- 消费提前停止应出现 abandoned/cancel，而不是继续在后台保留 stream。

## Stop 卡住

- 服务实现必须观察 CancellationToken；`[NonCancellable]` 长任务只能等业务自己结束或强制 stop timeout。
- Resolver watch、interceptor continuation、自定义 transport 和后台回调都必须响应取消并被 join。
- 观察 forced calls、background loop、deferred cleanup 和 framework cleanup timeout 日志。

## SharedMemory

- 只用于同用户同机；名称必须一致且不能含路径语义。
- 上次异常退出的 mapping/control 文件只能在确认无 live peer 后清理。
- ring 容量必须是范围内 2 次幂；高 spill/wait 表示容量或消费速度不匹配。

## AnonymousPipe

- offer 一次性使用，handle 不得记录。
- 跨进程在子进程继承后调用 `CompleteHandleTransfer`。
- 同进程验证必须保持 offer 到 client 完成，否则关闭本地副本会同时破坏 client I/O。

## 收集证据

报告 exact commit、OS/arch、.NET SDK/runtime、transport、配置、复现命令、结构化 code、Activity/metric/log 和最小可复现。性能问题还需固定 payload/concurrency/duration，并与同机交替基线对比。
