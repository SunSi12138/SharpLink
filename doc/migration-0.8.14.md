# SharpLink 0.8.14 迁移指南

English: [`en/migration-0.8.14.md`](en/migration-0.8.14.md)

0.8.14 不改变 public API、Protocol v2 或 generated Manifest。`NamedPipeServerTransportListener` 的 `maxServerInstances` 现在只接受 `NamedPipeServerStream.MaxAllowedServerInstances`（`-1`）或 1–254；Client `UseTcp`/`SocketClientTransportFactory` 的远端 TCP/DNS endpoint 不再接受端口 0，Server 的端口 0 临时绑定不变。依赖严格全局 flow-control waiter 顺序的代码应注意：当队首仅缺自身 stream credit 时，其他仍有 connection/stream credit 的 stream 现在可以前进；共享 connection credit 不足时仍保持 FIFO。
