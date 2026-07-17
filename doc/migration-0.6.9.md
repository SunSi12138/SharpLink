# SharpLink 0.6.8 → 0.6.9 迁移说明

0.6.9 没有公共 API 或 Protocol v2 wire format 变更。现有 0.6.8 Client、Server、契约、Generated Codec、Transport、TLS、认证、Interceptor、Hosting 与 DI 配置不需要修改源码。

需要注意的行为收敛：

- `ISharpLinkServer.StopAsync(gracefulTimeout, cancellationToken)` 现在严格有界。graceful timeout 后取消剩余服务调用，并在最多五秒框架清理预算后返回。
- 忽略 `CancellationToken` 的用户 Task 不再阻塞宿主退出。它仍拥有正在使用的 DI scope/provider，直到 Task 真实结束；框架 listener、session、Pipe 和 send queue 会按时释放。
- 提前退出 server/duplex stream 会更积极地发送 Cancel、回收 receive credit 并注销 dispatcher。正常完成的 stream 不发送额外 Cancel。
- Stream Dispatcher 池保留量有固定内部上限；这不是新的用户配置，也不改变流语义。
- OneWay 仍只保证完整调用已被本地有界 SendPump 接受，不表示服务端执行完成。发送队列饱和继续返回 `ResourceExhausted`。

升级后建议在预发布环境执行一次应用自己的滚动重启和“不合作服务”停机测试。框架提供的验证入口为 `SharpLink.ChaosTests`、`eng/run-release-soak.sh` 和 `eng/run-performance-matrix.sh`。
