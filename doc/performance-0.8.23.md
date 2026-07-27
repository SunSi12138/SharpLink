# SharpLink 0.8.23 性能验证

English: [`en/performance-0.8.23.md`](en/performance-0.8.23.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.22 commit `3a4338d` 与最终候选运行独立 Release 进程；每个 workload 预热 3 轮、采集 9 个样本，并交错复验 16-element array serialize/deserialize。

普通 `int[]` 最终候选约 10.1/17.0 ns，与基线 10.2–10.3/17.0 ns 重合，保持 0/88 B/op。`bool[]` serialize 回到基线约 7.5–7.6 ns；入站规范校验使 deserialize 从约 17.5 ns 增至 22.6 ns，即 16 个元素合计约 5 ns，保持 40 B/op。DateTimeOffset 专用 writer 约 15.4 ns、无分配且未见回退；完整 ticks/offset 校验使 16-element decode 增加约 23 ns，保持 280 B/op。

首版统一 write helper 令 `int[]` serialize 增至 12.6 ns，第二版仍约 10.8 ns，均被否决。最终通用 serializer 恢复原始直接 copy；只有 DateTimeOffset 注册专用 writer，入站校验则由 JIT 可常量折叠的类型门控进入。原始驱动保留在 `artifacts/performance/0.8.23-blit-collection-ab/`。
