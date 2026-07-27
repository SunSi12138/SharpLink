# SharpLink 0.8.30 迁移指南

English: [`en/migration-0.8.30.md`](en/migration-0.8.30.md)

0.8.30 不改变 Protocol v2、合法 payload 或正常 Host 启停顺序。

- 同一个 `SharpLinkServerHostedService` 实例在 Stop 开始后不能重新 Start，也不能重复 Start。标准 Generic Host 生命周期本来就满足此约束；手工调用 `IHostedService` 的测试或容器应为重启创建新 Host/DI scope。
- Stop 期间 Run 因 listener/server cleanup fault 结束时，Stop 仍返回原异常，但不再额外请求整个应用停止。
- `Task<T>` RPC 的 `T` 可以安全包含 `ValueTask` 字样；无需改契约。重新编译即可得到修正的 Proxy/Stub。
- `SharpLinkNamedPipeAddress.PipeName` 与 `SharpLinkSharedMemoryAddress.Name` 现在和具体 transport 一样拒绝 NUL、`/`、`\\`。把路径式名称改为无分隔符的逻辑标识。
- 本地 Server health check 的三个固定结果和描述不变，只复用完成 Task。
