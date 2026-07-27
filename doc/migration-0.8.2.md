# SharpLink 0.8.2 迁移指南

English: [`en/migration-0.8.2.md`](en/migration-0.8.2.md)

0.8.2 不改变合法 RPC 请求、响应或 generated contract layout。正常使用 SharpLink writer 的 peer 不需要 wire 迁移。

- 并发等待同一次 fixed-endpoint `ConnectAsync` 时，取消只影响对应调用者；Client 初始化继续运行到成功、失败或 shutdown。
- static/dynamic endpoint cluster 的握手超时仍以顶层 `Unavailable` 失败，但 inner cause 现在也是带明确 timeout 消息的 `SharpLinkException`。
- 自定义 DNS 查询实现的非 `SocketException` 不再由 last-good 隐藏，应由实现修复或由上层受监督循环记录/重试。
- 手写 peer 必须使用最短 VarUInt32 长度并发送合法 UTF-8 error message；SharpLink 自带 writer 已满足要求。

建议 Client/Server 同批升级，以便双方对 malformed wire 使用一致的拒绝规则。
