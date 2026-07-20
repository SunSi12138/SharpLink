# 迁移到 SharpLink 0.7.2

0.7.2 是与 0.7.1 兼容的性能版本。Protocol v2 wire format、公共 RPC API、Generator Manifest 格式、默认 `Singleton` 生命周期、运行时程序集注册和排空语义均未改变。

## 应用迁移

从 0.7.1 升级不需要修改代码或配置。重新还原 0.7.2 包并重新构建即可；Generator 和 Runtime 包应保持相同版本。

默认 Unary 调用现在直接返回内部池化 `ValueTask`。调用方仍应遵守 `ValueTask` 的标准约束：只等待一次，不在未转换为 `Task` 时并发消费同一个值。既有正常的 `await` 用法不受影响。

`WaitForReady=true`、取消、deadline、metadata、client/server interceptor、Activity/Meter、认证、授权、流控和动态程序集调用继续走各自完整语义路径。0.7.2 没有为了性能自动关闭任何能力，也没有放宽发送队列、Pending、Stream 或连接上限。

## 分配变化

同机 0.7.1→0.7.2 的 `Rpc_Add` BenchmarkDotNet 分配从 672 B/op 降为 360–364 B/op；五种传输的高并发进程级 Unary 分配从约 482–520 B/op 降为约 138–166 B/op。分配基线只与 0.7.1 比较，不使用 0.3.0 作为分配基线。

剩余分配主要来自服务端调用上下文快照、`ExecutionContext`/`AsyncLocal` 流动及其值映射。它们支撑 `SharpLinkCallContext.Current`、认证与授权的异步可见性，因此本版本保留。

完整环境、吞吐、尾延迟、分配和未采用实验见 [`performance-0.7.2.md`](performance-0.7.2.md)。
