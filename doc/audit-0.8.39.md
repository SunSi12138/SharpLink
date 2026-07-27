# SharpLink 0.8.39 深度审核

English: [`en/audit-0.8.39.md`](en/audit-0.8.39.md)

以 0.8.38 commit `d863dc3` 为精确基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | Server terminal 只记录成功；服务失败向 interceptor 展开时，Context 仍为 `Pending` 且没有 code/exception。 | terminal catch 在展开前统一记录 Failed/Cancelled、结构化 code、原始 exception 与 elapsed；外层 catch 保留兜底。 |
| P2 | 响应型 Server interceptor 可直接成功返回而不调用 `next`，服务端随后发送空 success，客户端才以 Codec 错误失败。 | 每层响应型 interceptor 成功结束后验证 single-use continuation 已调用；遗漏在服务端本地映射为 `Internal`。OneWay 行为不变。 |
| P2 | Client interceptor 返回错误类型的短路结果时，pipeline 先把 Context 记为 `Succeeded`，外层 typed cast 随后才失败。 | unary/client-streaming/streaming/OneWay 结果都在 tracked pipeline 内验证，只有合法形状才能发布成功。 |
| P2 | `SendClientStreamAsync` 是框架中唯一未使用 `ConfigureAwait(false)` 的应用 stream consumer；未完成的 `MoveNextAsync` 会回投调用方 Context。 | 框架侧 `await foreach` 显式不捕获 Context，保留同步 fast path，不改变用户 iterator 内部语义。 |
| P2 | generated request Codec、Stub 与 empty request 对 peer-controlled malformed bytes 抛 `InvalidDataException`，默认映射可能把数据损坏误报为 `Internal`。 | marker、截断、长度、required null、尾随数据与 empty request 全部在 wire trust boundary 抛结构化 `DataLoss`；应用自身 `InvalidDataException` 仍为 `Internal`。 |

预修复 Generator 的 117 个既有测试全部通过，新增 generated-wire 测试恰好失败；Unit 只有新增 empty-request 测试失败；Interceptor Integration 的 9 个既有测试全部通过，四个新增见证恰好失败，分别观察到 `Pending`、空 success、矛盾 `Succeeded` 和一次 Context `Post`。修复后 Generator 118/118、Unit 484/484、Integration 246/246；附加伪突变控制同时覆盖错误/合法的 stream 与 OneWay 短路形状。

真实 TCP interceptor harness 的三进程中位数从 41.267 降至 40.831 微秒（−1.06%），分配逐轮保持约 1,584.02–1,584.05 B/op；完整数据见 [`performance-0.8.39.md`](performance-0.8.39.md)。非增量 Release 构建为 0 warning / 0 error。

120 秒共享内存 Chaos 完成 837,357 success、330,087 expected、0 unexpected、23 次重启，Client/Server Error 均为 0，最大恢复 218 ms；最终 drain 与 connections/calls/pending/streams/send-queue 五项零指标通过。NativeAOT 输出 `AOT_SMOKE_PASS transport=tcp`；七个 0.8.39 包完成预提交打包，fresh-cache TCP/shared-memory functional smoke 通过。本轮仍发现新改进，连续无新改进轮次保持 0/3。
