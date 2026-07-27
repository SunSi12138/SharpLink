# SharpLink 0.8.22 性能验证

English: [`en/performance-0.8.22.md`](en/performance-0.8.22.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.21 commit `481989c` 与最终候选运行独立 Release 进程；每个 workload 预热 3 轮、采集 9 个样本，并按基线/候选交错复验。

Boolean serialize/deserialize 基线约 12.0–12.2/11.6–11.9 ns，候选约 11.5–11.7/10.4–10.5 ns；分配保持 0/24 B/op。包含 Rune、decimal、DateOnly、DateTime、TimeOnly、DateTimeOffset 的六字段 DTO serialize 基线约 38.0–38.5 ns，候选约 38.4–39.1 ns；deserialize 的稳定基线约 34.6–36.2 ns，候选约 36.3–37.5 ns。分配保持 0/80 B/op，新增全部校验的绝对成本约 1–2 ns。

首版把语义字段改为 length-delimited 内置 Codec，虽保持 0/80 B/op，却把 serialize/deserialize 提高到约 66/109 ns，已明确否决。最终方案保持 fixed wire，并让 JIT 内联各类型的快速校验；DateTimeOffset 只额外清零 padding。原始驱动保留在 `artifacts/performance/0.8.22-semantic-dto-ab/`。
