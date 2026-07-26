# SharpLink 0.8.19 迁移指南

English: [`en/migration-0.8.19.md`](en/migration-0.8.19.md)

0.8.19 不改变 Protocol v2 wire format 或 generated Manifest。自定义 Server authenticator 只有在 `IsAuthenticated=true` 且 `ErrorCode=Unknown` 时才会建立连接；直接调用 `SharpLinkAuthenticationResult` positional constructor 的实现应改用 `Success` 或 `Authenticate(context)`。

Client 与 Server interceptor 的 `next` delegate 现在每一级只能调用一次，第二次调用抛出 `InvalidOperationException`。需要 fan-out 的 interceptor 应在 `next` 之前完成自身并行工作，但只把一次逻辑 RPC 交给 pipeline；重试仍由 SharpLink retry policy 管理。

`SharpLinkAdmissionControlOptions.MaxQueueDelay` 现在最多为 2,147,483,647 ms（约 24.8 天），更大的值在配置校验期失败。超长 endpoint polling 与 Client/Server heartbeat interval 保持合法，并通过可取消分片等待。Generic Host Server Stop 在主失败与清理失败同时发生时会返回 `AggregateException`；Client faulted background task 也会新增 Error 日志。
