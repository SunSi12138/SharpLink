# SharpLink 0.8.14 性能验证

English: [`en/performance-0.8.14.md`](en/performance-0.8.14.md)

Apple M4 / .NET 10.0.2，以 0.8.13 commit `7e9c858` 和最终候选构建独立进程；双方统一关闭 tiered compilation，以反向顺序各运行两轮，每个工作负载 12 个测量样本。

无争用 flow credit acquire/update 基线中位数为 21.73/22.13 ns，候选为 21.58/21.84 ns，均 0 B/op。正常 producer pending register/complete 基线为 44.42/45.49 ns，候选为 45.46/44.81 ns，方向随进程互换且均为 48 B/op。短 ASCII named-pipe normalize 基线为 138.09/148.80 ns，候选为 139.28/144.16 ns，方向同样互换且均为 272 B/op，没有稳定回退信号。

首个 flow 候选在正常路径增加约 1–2%；最终方案把绕行扫描放入 no-inline 争用冷路径，并在再次加锁时验证原 stream state 身份，既恢复热路径，也避免 completion 竞态重新创建已关闭 stream。身份校验落地后的额外候选复测为 21.61/45.40/140.52 ns，分配仍为 0/48/272 B/op，继续落在逆序 A/B 波动区间内。原始驱动与日志位于 `artifacts/performance/0.8.14-transport-flow-ab/`。
