# 迁移到 SharpLink 0.7.4

0.7.4 新增协商压缩和可选的服务端主动接入控制。两项功能默认关闭；不修改配置时，公共调用语义、静态路由和服务生命周期不变。

## 协议兼容性

Protocol v2 minor 升为 3，HandshakeRequest/Response 增加压缩算法 token。0.7.4 不承诺与 0.7.3 及更早版本互操作，滚动升级应先确保连接两端都使用 0.7.4。两个 0.7.4 对端在双方关闭、只有一方启用或算法无交集时会正常退回未压缩连接。

Generated Manifest API 升为 2，因为 admission control 需要生成期的 `ClientStreamCount`。0.7.3 及更早版本预编译的生成程序集会在注册时以明确的 Manifest 不兼容错误被拒绝；升级到 0.7.4 时必须重新运行 Source Generator 并重新编译这些程序集。

## 启用压缩

在 Client 与 Server 的 `UseRuntime` 中按偏好添加 Provider。内置 Gzip、Deflate、Brotli 只依赖 `System.IO.Compression`；自定义 Provider 必须线程安全、NativeAOT 安全，完整消费输入并准确报告 written bytes。框架仅在达到 payload、最小字节收益和最小比例三个阈值时发送压缩候选。

协商 token 表示 wire profile，不是算法参数字典。内置 `CompressionLevel` 只影响本地方向的编码成本和压缩比，因此两端可以不同且不进入握手；dictionary identity、window/profile 限制等影响解码的设置必须编码到不同 token，并注册为不同 Provider。不要让两个不兼容配置复用同一 token。

## 启用 admission control

服务端调用 `UseAdmissionControl` 并至少配置一个 Global、Contract、Method 或 Partition 限制。等待只有在 `MaxQueuedCalls`、`MaxQueuedBytes` 和 `MaxQueueDelay` 三者都为正数时启用；任一缺失会在 Build 前明确失败。公共配置模型不暴露 `System.Threading.RateLimiting` 类型，但 `SharpLink.Server` 包新增该成熟实现的 10.0.2 运行时依赖。

OneWay 默认立即拒绝而不排队。此前把 OneWay 本地发送完成当成服务已执行的代码需要修正：本地完成始终只表示 SendPump 接受帧。确实需要排队时显式设置 `QueueOneWayCalls=true`，并为它预留相同的 call/byte 预算。

分区 selector 返回 null 或空字符串时进入默认分区。请只返回有界、稳定的业务键；真实键不会进入指标。达到 `MaxPartitions` 且没有超过 idle timeout 的安全空闲 entry 时，新键收到 `ResourceExhausted(partition_capacity)`。

## 可观测性与容量

新增六个 `sharplink.admission.*` 指标。升级后先以只配置较宽的立即 permit 规则观察实际 active/queue 数据，再逐步收紧；队列是削峰工具而不是无限缓冲，必须同时按请求数、保留字节和最长等待设置界限。
