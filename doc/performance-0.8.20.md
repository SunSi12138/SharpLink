# SharpLink 0.8.20 性能验证

English: [`en/performance-0.8.20.md`](en/performance-0.8.20.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.19 commit `2d7cd95` 和最终候选构建运行独立进程，关闭 tiered compilation。valid generated string workload 每进程预热后运行 9 个样本；连续输入每样本 2,000,000 次，分段输入每样本 500,000 次，并用候选与基线交错复验。

最终方案的连续输入 median 为 33.74–34.98 ns/op，基线为 34.31–34.36 ns/op，区间交叠且双方均为 64 B/op。分段输入候选 median 为 123.71–127.86 ns/op，基线为 120.99–122.11 ns/op，双方均为 112 B/op；replacement marker scan 的稳定成本约 3.5 ns（约 3%），只影响 generated string decode，不新增分配。

审核同时实测并否决两个更简单的方案：始终使用 exception-fallback decoder 约慢 8%，先完整调用 `Utf8.IsValid` 再解码约慢 10%。最终实现只在正常解码结果含 U+FFFD 时严格复核原始 bytes。这是完整区分合法 U+FFFD 与畸形 UTF-8 所需的最小实测代价，避免为了数纳秒维护自制 UTF-8 decoder。原始驱动位于 `artifacts/performance/0.8.20-utf8-ab/`。
