# SharpLink 0.8.2 深度审核

English: [`en/audit-0.8.2.md`](en/audit-0.8.2.md)

本批以 0.8.1 commit `5d30863` 为基线，集中检查 Client 连接生命周期、endpoint discovery 和 Protocol v2 文本/长度边界。五项 P2 均先加入反例；修复前 Unit 344 项中恰好 5 项失败，修复后全部通过。

| 等级 | 问题与实证 | 修复与验证 |
| --- | --- | --- |
| P2 | fixed-endpoint 首次 `ConnectAsync` 把首个调用者 token 传给共享初始化；该调用者取消会同时取消未取消的并发 waiter。 | 共享初始化仅由 Client shutdown 控制，每个调用者只取消自己的 `WaitAsync`；阻塞 transport 并发测试证明 surviving waiter 最终进入 Ready。 |
| P2 | static/dynamic endpoint cluster 的握手 timeout 直接保留 linked-token `OperationCanceledException`，与 fixed client 的结构化 `Unavailable` 不一致，诊断链无法识别 timeout。 | 三种连接模式复用同一私有握手完成路径；cluster 失败链保留明确的 `RPC handshake timed out` cause。 |
| P2 | DNS 已有 last-good 后捕获所有异常；Resolver 实现中的 `InvalidOperationException` 也会在 Resolve/Watch 中被永久吞掉。 | last-good 只处理 BCL DNS 的瞬时 `SocketException`；非 DNS 异常回到调用方或外层受监督 worker。 |
| P2 | `TryReadVarUInt32` 接受 `80 00` 等 overlong 长度，导致 metadata/error 长度存在多个 wire 表示。 | 终止 byte 为零且不是首 byte 时拒绝；直接 metadata decode 和完整 Request frame 均有反例。 |
| P2 | Binary error 使用宽松 UTF-8 decoder，非法 peer bytes 被替换为 U+FFFD；frame shape validation 也未检查文本。 | contiguous/segmented payload 统一使用严格 UTF-8，frame validation 与直接 decode 均报告 `ProtocolViolation`。 |

有效 wire layout 不变。性能见 [`performance-0.8.2.md`](performance-0.8.2.md)，兼容说明见 [`migration-0.8.2.md`](migration-0.8.2.md)。
