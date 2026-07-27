# SharpLink 0.8.33 迁移指南

English: [`en/migration-0.8.33.md`](en/migration-0.8.33.md)

0.8.33 不改变公共 API、Protocol v2 framing、route hash 或合法 payload。它收紧无法由一个生成类正确实现的契约形状，并修复失败回滚与 Generic Host 启动所有权。

## Generator

继承层次中参数签名相同但返回类型不兼容的 RPC 方法现在报告 `SHARPLINK057`，并且不生成该冲突契约的 Proxy/Stub。此形状此前生成的类无法同时实现两个接口成员；应重命名其中一个路由、统一返回类型，或拆分契约。生成 Stub 的内部 size-field 名称改变，但 wire 类型、Manifest identity、route hash 与 payload 均不变。

## Builder 与 Generic Host

- Client/Server 同步 `Build()` 失败时仍会在返回前完成异步资源清理并聚合清理异常，但不再依赖调用线程泵送其 `SynchronizationContext`。
- Client 与 Multi-Cluster Hosted Service 都是单次启动所有者。第二次 `StartAsync` 现在稳定抛出 `InvalidOperationException`，不会释放已有实例，也不会让 accessor 进入失败状态。
