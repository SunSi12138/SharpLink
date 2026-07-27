# SharpLink 0.8.43 深度审核

English: [`en/audit-0.8.43.md`](en/audit-0.8.43.md)

以 0.8.42 commit `cd2de157` 为精确基线，本批确认一项 P1 与四项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P1 | 每个共享内存 Server 创建前都清理同目录文件；并发创建者会删除另一个仍在初始化或尚未被 Client 打开的新 mapping。 | 进程内串行化 cleanup/初始化，并只回收至少一分钟前的可独占打开文件；五分钟前的 stale 对照仍会删除。 |
| P2 | 每个接收 stream item 更新 credit 后都会再次进入同一 flow-control gate，即使跨流 update queue 几乎总为空；duplex 每 RPC 约多 512 次锁。 | nullable queue 作为无等待者信号；空时无锁返回，取走最后一项后恢复 null fast path。 |
| P2 | `ConnectionClosed` completion 没有显式异常时退回通用 `Internal`。 | 在 PendingRequestTable 的完成边界合成保留 `ConnectionClosed` code 的结构化异常。 |
| P2 | Client response stream 在远端终态前被消费者释放时，Activity 与 completed counter 仍按成功记录。 | 区分已观察终态与提前释放；后者记录 `OperationCanceledException` Error 和 `consumer_abandoned`。 |
| P2 | 动态 selector 持有旧 snapshot 跨过 generation release 后恢复，会在发现连接失效前重建已退役 breaker 状态，且不再有后续 Retire。 | 失效选择在确认 generation 已释放后再次执行幂等 lifecycle retirement，闭合两个竞态顺序。 |

共享内存前置证据包含实际 `File.SetUnixFileMode` 时序失败、64×8 并发压力和确定性 fresh-peer 删除见证。连接关闭的确定性 case 与既有 512 路 dispose/register 竞态在基线均失败；提前流释放的 Client Activity 在基线报告成功；动态端点探针暂停旧 snapshot、等待首次 Retire 后恢复，基线明确留下一个活跃 generation。修复后这些聚焦用例全部通过。

0.7.11/当前版本的独立性能调查把 Balanced duplex 的首个一致回退定位到 0.8.0，并证明每 item 空 drain 的第二次锁是根因：仅移除该空操作的三组 A/B 为 +6.7%、+9.6%、+3.7%，配对中位 +6.7%，CPU/stream -8.8%。0.8.42/candidate 三组交替完整 streaming A/B 的配对中位为 c2s +1.5%、s2c -1.8%、duplex +4.0%，unary control -0.6%，全部零失败且无实质延迟回退。MemoryDiagnoser 同时纠正了 LoadTest 的 B/item 归一化假象，不宣称不存在的 allocation 回退。

最终非增量 Release 为 0 warning / 0 error，Generator 121/121、Unit 496/496、Integration 252/252。120 秒共享内存 Chaos 完成 812,602 success、325,934 expected、0 unexpected、23 次重启，Client/Server Error 均为 0，最大恢复 324 ms；drain 与五项活跃指标全部归零。NativeAOT TCP 通过；七包 pack 与 fresh-cache TCP/shared-memory functional smoke 作为同一最终门禁执行。本轮仍发现新改进，连续无新改进轮次保持 0/3。
