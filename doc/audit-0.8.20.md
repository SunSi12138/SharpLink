# SharpLink 0.8.20 深度审核

English: [`en/audit-0.8.20.md`](en/audit-0.8.20.md)

以 0.8.19 commit `2d7cd95` 为基线，本批确认五项 P2 以上改进：RPC、TLS 与 shared-memory 握手超时接受超出便携原生 timer 范围的配置；断线 `WaitForReady` 的超远 deadline 在 `Task.WaitAsync` 中立即失败；满 pending table 的同类 deadline 在 `SemaphoreSlim.WaitAsync` 中立即失败；Server graceful Stop 的饱和单调时钟 deadline 被直接交给原生等待并触发强制停止；generated DTO string 使用 replacement UTF-8 decoder，静默把畸形 wire bytes 改写为 U+FFFD。

完整前置 Unit 探针共 441 项：原有 436 项全部通过，新增五项恰好全部失败。两个 Client deadline 探针捕获到立即 `ArgumentOutOfRangeException`，Server 探针观察到 owner 完成前 wait 已结束或 fault，三类握手配置被错误接受，连续与分段畸形 UTF-8 都没有报告 `DataLoss`。这些结果把每项建议限定为可重现的外部行为，而不是仅凭静态风格判断。

最终实现统一复用便携 timer 分片：握手配置在取得连接或 transport 所有权前拒绝越界值，远期 readiness、pending admission 和 Server drain 保持可取消、可完成。generated string 保留正常 `Encoding.UTF8` 解码，仅当结果含 U+FFFD 时以 strict decoder 复核原始 bytes，既拒绝畸形输入，也允许合法编码的 U+FFFD。

修复后非增量 Release 构建为 0 warning / 0 error，Generator 83/83、Unit 441/441、Integration 230/230、七包打包与全新缓存 package smoke 全部通过。迁移见 [`migration-0.8.20.md`](migration-0.8.20.md)，性能见 [`performance-0.8.20.md`](performance-0.8.20.md)。
