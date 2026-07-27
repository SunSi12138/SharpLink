# SharpLink 0.8.27 迁移指南

English: [`en/migration-0.8.27.md`](en/migration-0.8.27.md)

0.8.27 不改变 Protocol v2 framing 或合法 payload layout，但收紧两种错误响应。声明 `HasResponsePayload` 的调用不再把空响应静默转换为 `default(T)`，而是让注册 Codec 决定其是否合法；不带 payload 的 void/acknowledgement 若收到额外字节则报告 `DataLoss`。自定义 Codec 若将空序列定义为合法值，行为仍由该 Codec 决定。

对 server/duplex response stream 调用 `WithCancellation` 或显式 `GetAsyncEnumerator(token)` 时，consumer token 与原始 RPC call token 现在同时有效。无需改代码；此前依赖 consumer token 屏蔽调用取消的行为不受支持。

`AnonymousPipeClientTransportFactory` 的 handle offer 从首次 `ConnectAsync` 开始即被消费，即使建连失败也不能重试。失败后请向 `IAnonymousPipeAllocator` 请求一组新 handle。Hosted Server 若在 Host 未停止且 Hosted Service 未执行 `StopAsync` 时自行正常退出，现在会触发 `StopApplication`；需要单独停 Server 时，应同步进入 Host/Hosted Service 的标准停止流程。

writer pool 的并发 Dispose 修复对调用方透明，public API 与配置无需变化。
