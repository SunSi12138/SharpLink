# SharpLink 0.8.34 性能验证

English: [`en/performance-0.8.34.md`](en/performance-0.8.34.md)

Apple M4 / .NET SDK 10.0.102，以 0.8.33 commit `35c8cd2` 的独立 worktree 为基线。所有夹具使用独立 Release 进程、5 次预热与 101 次测量。

## Shared-memory reader 快路径

| 版本 | Median | P25–P75 | 分配 |
|---|---:|---:|---:|
| 0.8.33 baseline | 29.564 ns | 29.414–29.881 ns | 40 B/sample |
| 0.8.34 final | 30.046 ns | 29.770–30.118 ns | 40 B/sample |

reader 的每次读完成增加一个仅在 completion 后进入慢路径的 volatile 检查，中位数增加 0.482 ns（1.63%），分配不变。一个在入口和 finally 都执行额外 activity 检查的先行方案回退 7.9%，已撤销。

## 继承契约 Generator

夹具包含 40 个契约、每个契约两个基接口与 10 个重复 RPC。最终以 baseline→candidate 与 candidate→baseline 两种顺序运行：

| 顺序/版本 | Median | P25–P75 | 当前线程分配中位数 |
|---|---:|---:|---:|
| baseline（顺序 1） | 17.084 ms | 15.972–24.541 ms | 30,723,280 B |
| final（顺序 1） | 15.806 ms | 15.518–19.485 ms | 30,652,648 B |
| final（顺序 2） | 17.497 ms | 15.840–25.298 ms | 30,656,080 B |
| baseline（顺序 2） | 17.853 ms | 15.627–23.888 ms | 30,717,032 B |

两轮 process median 的中位数为 17.469 -> 16.652 ms（改善 4.68%），分配为 30,720,156 -> 30,654,364 B（改善 0.21%）。最初逐 pair 重复解析 Attribute 的方案曾回退 39.9%；另一方案在单轮中超过 5% 且顺序不稳定，均已撤销。最终实现按 CLR 签名代表项线性比较，每个方法只解析一次相关策略，且不重复执行已由 `SHARPLINK050` 负责的 Timeout 合法性验证。

日志分类、Chaos oracle 和终止态 `AdvanceTo` 只进入失败/teardown 路径，不改变正常 RPC、序列化或 send-pump 热路径。原始夹具保留在 `artifacts/performance/0.8.34-reader-ab/`、`artifacts/performance/0.8.34-generator-ab/` 与 `artifacts/performance/0.8.34-baseline/`。
