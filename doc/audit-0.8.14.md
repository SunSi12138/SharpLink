# SharpLink 0.8.14 深度审核

English: [`en/audit-0.8.14.md`](en/audit-0.8.14.md)

以 0.8.13 commit `7e9c858` 为基线，本批确认五项 P2 以上问题：Unix named pipe 以 UTF-16 字符而非 UTF-8 字节计算 native path；listener 只拒绝 instance limit 0；Client stream producer 的抛错取消回调会打断 pending completion；Client TCP 接受仅供 Server 临时绑定使用的端口 0；flow-control 全局 FIFO 会让只缺自身 stream credit 的队首阻塞其他额度充足的 stream。

预修复完整探针保留了 0.8.13 的 404 项通过结果，并得到 6 个失败 case：Unicode path、instance limit 的 `-2`/`255`（同一问题）、producer callback、Client port 0，以及一个初期 multi-cluster 假设。后者经 token 所有权复核后撤回；替代它的跨流队头阻塞由旧全局 FIFO 分支与反转后的进度断言直接实证。最终实现按完整 UTF-8 path 预算安全裁剪；在构造期冻结 transport 契约；把 producer 回调异常隔离到 no-inline 冷路径并经 Client logger 上报；以及只在 connection credit 不足时保留全局 FIFO。修复后 Unit 411/411，并通过完整发布门禁。

迁移见 [`migration-0.8.14.md`](migration-0.8.14.md)，性能见 [`performance-0.8.14.md`](performance-0.8.14.md)。
