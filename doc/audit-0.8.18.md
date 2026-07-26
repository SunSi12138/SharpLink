# SharpLink 0.8.18 深度审核

English: [`en/audit-0.8.18.md`](en/audit-0.8.18.md)

以 0.8.17 commit `f7d4b8d` 为基线，本批确认五项 P2 以上问题：Hosted Client 在 token-bound Stop 被取消后丢失已转移 owner；超长 dynamic-module graceful timeout 超出原生 delay 范围并把模块留在 Draining；超长 send flush latency 的 stopwatch 转换溢出为即时 flush 或 pump fault；Server call concurrency 接受可让首次 deadline scan 请求多 GB 数组的 `int.MaxValue`；一个抛错 stream dispatcher 会阻断 sibling stream 与 Session transport 清理。

预修复完整 Unit 共 432 项，原有 427 项全部通过，五个聚焦探针恰好全部失败。探针直接观察未调用的 Client Dispose、lease 释放前已失败的 unregister、faulted send pump、被接受的无界 call 配置，以及未完成的 sibling dispatcher 与 transport。最终实现保证 Hosted Stop 后释放 owner、复用有界的超长 timer 分片、饱和 flush monotonic deadline、在 public/internal 两层限制 call snapshot，并在锁外完整排空 dispatcher 后才传播异常；RpcSession 终止路径隔离用户清理异常。

修复后非增量 Release 构建为 0 warning / 0 error，Generator 83/83、Unit 432/432、Integration 228/228、七包打包与全新缓存 package smoke 全部通过。专项性能扫描未把 generator 冷路径字符串、构建/拓扑 LINQ 或兼容性 inheritance surface 误判为运行时 P2。

迁移见 [`migration-0.8.18.md`](migration-0.8.18.md)，性能见 [`performance-0.8.18.md`](performance-0.8.18.md)。
