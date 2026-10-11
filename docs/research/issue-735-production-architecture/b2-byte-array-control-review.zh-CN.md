# Issue #735：B2 byte-array 控制组的独立审计与完整配对结果

**三个控制组共 216/216 个进程成功，2,260,992 条逐 RPC 原始样本独立复算通过；八项 B2 报告计数在每个进程中均为 0。** 这说明原 byte-array workload 没有进入本次 server-sized grant 路径。正向和负向差值均保留，不能把未启用路径的正数当作 B2 grant 已达到性能目标。完整 96 个 A/B pair、12 个 A/A pair、两侧绝对数值、计数及原样本 hash 见 [机器可读配对全集](b2-byte-array-control-pairs.json)。

本报告只审计 byte-array。B2 pilot 的 No-Go 判断、eligible DTO 的真实结果及缺失的完整验收门槛见 [真实 RPC 方法与结果](b2-real-rpc-methods-and-results.zh-CN.md)；不能用本控制组的任何数字抵消那里的失败或缺失。

## 1. 实际输入、编译身份与原始产物

两臂共同包含独立正确性修复 #849 / #850 / #851：A 是 [c7ba1768](https://github.com/SunSi12138/SharpLink/commit/c7ba1768f31ce7219dc438dc6009347de6806ed5)，B 是 [5cabd4d7](https://github.com/SunSi12138/SharpLink/commit/5cabd4d7cc5ce9aa0a87424e36954e414e0912da)。B 的四个生产文件与 ba9 相同；5cab 仅修复新 DTO 测量程序私有参数的 nullable 注解。这是 c7→B2 增量对照，未测出 f8→最终组合的总收益。#840/#841 已在原始 f8 中，未计入 B2。

| runtime population | 实际 job | Artifact | 原 ZIP 字节 | ZIP / 解压核验 | 进程 / 原始 RPC |
|---|---|---:|---:|---:|---:|
| JIT PGO OFF | [success](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/job/114572398503) | [11715875656](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715875656) | 25,124,332 | 592 文件 | 72 / 753,664 |
| JIT PGO ON | [success](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/job/114572398584) | [11715117995](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715117995) | 25,055,191 | 592 文件 | 72 / 753,664 |
| NativeAOT | [success](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/job/114572398422) | [11715028078](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315/artifacts/11715028078) | 49,560,974 | 564 文件 | 72 / 753,664 |

三份原 ZIP 的 SHA256：

- JIT PGO OFF：`5924bc14153c3ab8ae4d96a688efcbb6dc5ab15fb1f463abd1af4bef2583ff35`。
- JIT PGO ON：`6a0522cabd31616349aeda98c0083e7016efbc65e17da0f452617b634c89ab1d`。
- NativeAOT：`ba5cedaf0967e0bf455c9cce50b5de4f5dd7b519f5265704a20b96e29be6bf70`。

每组独立核对 A 的 496 个与 B 的 497 个生产 Git blob，确认只有获批四文件差异；另核对两臂相同的 18 项测量输入、原有合约/服务/构建策略、编译身份文件和不变的 SendPump。JIT 每组保留并核对 56 个构建输出文件，NativeAOT 为 28 个；实际运行应用 DLL/native ELF 另与每个进程报告、启动命令和 plan 的 post-build hash 对应。原编译、退出和 stderr 日志均保留。

| 实际执行主体 | A SHA256 | B SHA256 |
|---|---|---|
| JIT 应用 DLL（OFF/ON 相同） | `5a9023aa18dfe0ae949646e4c42570b915fbe660e9c852b4e7213272d8c53134` | `a89c8aa4edf52a2be4070aec8ccec6ddaad1201cbfc7ef9715aad7ba56672709` |
| NativeAOT ELF | `523f9b80c7a542446d2fe3860d97bafedfc15eaa151d97dc3b380b85c7ccac20` | `64e9f6d91d7db8891cc0047650770f3dee15410f6a5b46415c7f06638e515c4b` |
| JIT Runtime DLL（OFF/ON 相同） | `32e5dbd24b06d772807c986a8cd708e694339ca7902251e22f0d8b01da244f6f` | `a33187fa89aacb4dd949fbb717824be5171128e8b2cf4feb649afdf735d64399` |

运行环境为 Ubuntu 24.04.5 LTS x64、实际 .NET 10.0.12、四核 affinity、ThreadPool minimum 32。JIT 两组分别回报 TieredPGO=0/1、TieredCompilation=1、ReadyToRun=0；COMPlus 覆盖均未设置。NativeAOT 有实际 publish/native-code 编译日志，直接执行保存的 ELF，builtForNativeAot=true 且 dynamicCodeSupported/Compiled=false。三个 job 的相同 machine 字符串不构成跨 job 同一物理 runner 的证据，未混合其绝对吞吐。

### JIT PGO ON 原 ZIP 的完整性恢复

独立审计首次读取该组 `rpc-screen.zip` 时发现只有 **22,151,168 B**，SHA256 为 `b0b792823946b4cc29df92c32e8e98e26d554be4b75547c7484baeea0193e302`，与 GitHub 元数据不符，立即停止该组 ZIP gate。该不完整文件原样保留；原因没有被推定为 RPC 或调度器失败。随后从**同一 Artifact 11715117995** 下载到新文件 `rpc-screen-original-11715117995.zip`，核对 25,055,191 B、GitHub SHA256 与完整 CRC，并逐字节确认 592 个既有解压原文件完全相同。独立 reader 通过 `--archive` 显式使用这份合格原件后才完成验收。恢复未执行任何新 RPC，也未更换样本；`original-zip-recovery.json` 与 `original-zip-recovery-raw-comparison.json` 保留过程。

## 2. 负载与独立统计边界

每 runtime 分开执行 TCP、SharedMemory。Unary 为 1×16 B、c1、每 worker 32,768 RPC；Server/Client/Duplex 均为 1,000×16 B、c128、每 worker 32 RPC。这里 16 B 是应用数组，实际 BlitArrayCodec<byte> 编码为 20 B。每个进程 preflight 明确回报 CanExactSize=false，验证完整 payload roundtrip；[真实 eligibility 与 lease 捕获入口](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/src/SharpLink.Runtime/RpcSession.GeneratedServerBridge.cs#L246-L300)因此不会捕获 B2 lease。

四个 shape×两传输×两轮×ABBA/BAAB 四位置 = 64 个 A/B 进程；Server 的两传输×两轮×左右两个位置再给出 8 个 A/A 进程。每个 A/B cell 的四 pair 固定为 r0 的 p0/p1、p2/p3，及 r1 的 p0/p1、p2/p3，统一换算成 B/A；A/A 统一为 right/left，第二轮反转实际执行顺序。所有实际进程的 UTC 启动与 wallSeconds 证明这些匹配位置没有并行重叠。

程序使用同一连接上的持久 worker，每 worker 逐 RPC 记录开始、验证完成和原始 Stopwatch ticks；独立核对每 worker 次序、无重叠 RPC、公共 gate、全部样本数、worker 均值/跨度及六个时点的完成分布。预热同时满足每 worker 至少 2 RPC 和 5 s；没有因此宣称 JIT tier 已完全稳定。窗口、Flush、socket 和原有内存限制保持原配置。该 benchmark 的禁用 request timeout、SharedMemory heartbeat 5/10 min 与外部 60 s watchdog 均按原测量方法披露，不替代 Deadline/取消测试。

吞吐从成功 RPC 数与实际 completed wall time 重算；CPU/RPC、B/RPC 从原进程总量除以 RPC 数重算，范围包含同进程客户端、服务端和完整验证。另核对包含 WaitForIdle 观察与中间计数读取成本的 settled 区间。raw 文件 I/O、额外 session probe、report 格式化及 transport Dispose 在成本区间之外；成功报告在实际 Dispose 成功后才发布。calls/streams 为 0 不证明所有 late WU、SendPump 或信用已结清。

逐 RPC nearest-rank P50/P95/P99/P99.9/max 与所有原报告相符。流式每进程 4,096 RPC 的 N/1000 为 4.096；这是名义尾部数量，并非有效独立样本量。Jain 完成分布也由 timestamps 独立重算，100% Jain=1 因为每 worker 固定相同完成数；它不能证明信用 FIFO 公平性。

## 3. 全部 A/B 吞吐 pair 与成本结果

以下均为百分比。四 pair 按上文固定顺序完整列出，正数为 B/A 增加；CPU/RPC 与 B/RPC 增加意味着成本上涨。成本列为四个配对差值的中位数，不是两臂中位数的比值；完整绝对值、所有成本/尾延迟 pair、范围和几何均值均在配对 JSON。没有删去负向 pair。

### JIT PGO OFF

| transport / shape | RPC/s 四个原 pair Δ% | RPC/s 中位 Δ% | CPU/RPC 中位 Δ% | B/RPC 中位 Δ% |
|---|---|---:|---:|---:|
| sharedmemory / Client | +3.288 / +5.610 / +2.596 / +2.992 | +3.140 | -2.728 | -4.113 |
| sharedmemory / Duplex | +1.753 / -2.218 / +1.635 / -3.775 | -0.291 | +0.737 | -0.284 |
| sharedmemory / Server | -3.712 / -1.609 / +1.606 / -0.620 | -1.115 | +0.834 | +1.966 |
| sharedmemory / Unary | -1.514 / -1.467 / +2.450 / -5.844 | -1.490 | +1.027 | -0.018 |
| tcp / Client | +0.696 / +5.463 / -2.236 / +1.968 | +1.332 | -0.758 | +0.335 |
| tcp / Duplex | -1.256 / +1.103 / -0.743 / -3.448 | -0.999 | +1.738 | +2.670 |
| tcp / Server | -0.237 / +2.984 / -0.062 / +4.816 | +1.461 | -0.186 | -5.922 |
| tcp / Unary | -4.279 / +5.686 / -3.643 / -1.519 | -2.581 | +1.420 | +0.000 |

### JIT PGO ON

| transport / shape | RPC/s 四个原 pair Δ% | RPC/s 中位 Δ% | CPU/RPC 中位 Δ% | B/RPC 中位 Δ% |
|---|---|---:|---:|---:|
| sharedmemory / Client | +0.145 / -1.020 / +2.120 / -2.289 | -0.437 | -0.133 | +2.028 |
| sharedmemory / Duplex | +3.948 / +2.305 / -8.108 / -0.966 | +0.669 | -0.882 | +0.223 |
| sharedmemory / Server | +3.119 / +2.082 / +2.914 / +4.636 | +3.016 | -2.410 | -10.848 |
| sharedmemory / Unary | -0.721 / +0.680 / +0.941 / +2.155 | +0.810 | -0.904 | -0.029 |
| tcp / Client | -1.457 / -5.716 / -1.550 / -1.784 | -1.667 | +1.006 | -0.184 |
| tcp / Duplex | -1.705 / +0.030 / +1.655 / -1.681 | -0.825 | +0.719 | +0.074 |
| tcp / Server | -0.626 / +6.578 / +3.707 / -0.147 | +1.780 | -0.556 | -3.029 |
| tcp / Unary | +5.965 / +4.093 / -1.531 / +9.183 | +5.029 | -2.537 | +0.000 |

### NativeAOT

| transport / shape | RPC/s 四个原 pair Δ% | RPC/s 中位 Δ% | CPU/RPC 中位 Δ% | B/RPC 中位 Δ% |
|---|---|---:|---:|---:|
| sharedmemory / Client | -0.545 / -0.170 / -0.009 / -2.568 | -0.357 | +0.948 | -4.146 |
| sharedmemory / Duplex | -2.819 / +2.087 / +0.021 / -1.234 | -0.607 | +1.474 | -3.308 |
| sharedmemory / Server | -0.325 / +2.786 / -1.366 / -3.958 | -0.845 | +1.094 | -6.856 |
| sharedmemory / Unary | +1.097 / -1.546 / -0.550 / -0.765 | -0.658 | +0.816 | -0.005 |
| tcp / Client | +6.088 / +5.666 / -4.849 / -1.481 | +2.092 | +0.248 | +1.093 |
| tcp / Duplex | -0.539 / +3.603 / -3.544 / +4.559 | +1.532 | +0.920 | +1.544 |
| tcp / Server | +4.186 / -0.493 / +4.734 / +1.605 | +2.895 | -0.603 | -7.933 |
| tcp / Unary | +4.893 / -1.200 / -2.888 / +3.268 | +1.034 | -1.552 | +0.000 |

## 4. settled 与尾延迟结果

下表为同一四 pair 的中位差值。P99/P99.9 的正数表示变慢；JSON 保留全部原 pair 和最大/最小值，不能由四个过程级估计推出生产尾延迟保证。

| runtime | transport / shape | settled RPC/s Δ% | settled CPU/RPC Δ% | settled B/RPC Δ% | P99 Δ% | P99.9 Δ% |
|---|---|---:|---:|---:|---:|---:|
| JIT PGO OFF | sharedmemory / Client | +3.138 | -2.728 | -4.113 | -6.004 | -11.187 |
| JIT PGO OFF | sharedmemory / Duplex | -0.291 | +0.738 | -0.284 | -1.069 | -1.621 |
| JIT PGO OFF | sharedmemory / Server | -1.114 | +0.834 | +1.966 | -1.150 | -1.236 |
| JIT PGO OFF | sharedmemory / Unary | -1.490 | +1.027 | -0.018 | +2.126 | +2.638 |
| JIT PGO OFF | tcp / Client | +1.332 | -0.757 | +0.335 | +0.194 | +0.586 |
| JIT PGO OFF | tcp / Duplex | -0.999 | +1.738 | +2.670 | -9.891 | -10.084 |
| JIT PGO OFF | tcp / Server | +1.461 | -0.186 | -5.922 | -2.130 | -1.055 |
| JIT PGO OFF | tcp / Unary | -2.582 | +1.420 | +0.000 | +0.729 | +0.888 |
| JIT PGO ON | sharedmemory / Client | -0.437 | -0.133 | +2.028 | -2.163 | -9.407 |
| JIT PGO ON | sharedmemory / Duplex | +0.669 | -0.883 | +0.223 | +1.964 | -1.668 |
| JIT PGO ON | sharedmemory / Server | +3.016 | -2.408 | -10.848 | -9.629 | -11.923 |
| JIT PGO ON | sharedmemory / Unary | +0.811 | -0.905 | -0.029 | -1.460 | +1.979 |
| JIT PGO ON | tcp / Client | -1.667 | +1.006 | -0.184 | +3.533 | +4.308 |
| JIT PGO ON | tcp / Duplex | -0.825 | +0.719 | +0.074 | +0.626 | +2.419 |
| JIT PGO ON | tcp / Server | +1.780 | -0.556 | -3.029 | -4.048 | -4.327 |
| JIT PGO ON | tcp / Unary | +5.029 | -2.537 | +0.000 | -2.076 | -7.429 |
| NativeAOT | sharedmemory / Client | -0.358 | +0.949 | -4.146 | -2.689 | +7.408 |
| NativeAOT | sharedmemory / Duplex | -0.607 | +1.474 | -3.308 | +2.795 | +2.413 |
| NativeAOT | sharedmemory / Server | -0.846 | +1.094 | -6.856 | +3.577 | +5.479 |
| NativeAOT | sharedmemory / Unary | -0.656 | +0.816 | -0.005 | -0.332 | -1.500 |
| NativeAOT | tcp / Client | +2.093 | +0.248 | +1.093 | -6.825 | -8.326 |
| NativeAOT | tcp / Duplex | +1.532 | +0.920 | +1.544 | -3.365 | -3.943 |
| NativeAOT | tcp / Server | +2.896 | -0.604 | -7.933 | +1.190 | -0.033 |
| NativeAOT | tcp / Unary | +1.033 | -1.552 | +0.000 | -0.617 | -2.616 |

## 5. 全部 A/A pair：只用于各自 Server cell

每一格为 r0 / r1 的两个原 right/left 差值。它们没有变成零点修正，也没有被从 A/B 中相减。Unary/Client/Duplex 未做各自的 A/A，因此不能拿 Server 的 A/A 当成所有 shape 的噪声带。

| runtime / transport | RPC/s 两 pair Δ% | CPU/RPC 两 pair Δ% | B/RPC 两 pair Δ% | P99 两 pair Δ% | P99.9 两 pair Δ% |
|---|---|---|---|---|---|
| JIT PGO OFF / sharedmemory | +0.711 / -2.646 | -0.665 / +2.212 | +7.056 / -0.353 | +1.774 / -7.902 | +0.075 / -9.641 |
| JIT PGO OFF / tcp | -5.685 / +0.249 | +5.177 / -0.458 | +5.473 / -0.246 | +0.565 / +0.508 | +3.613 / +1.176 |
| JIT PGO ON / sharedmemory | -0.983 / +3.490 | +1.890 / -3.740 | -0.187 / -5.342 | +9.890 / -3.720 | +5.277 / -1.903 |
| JIT PGO ON / tcp | -4.287 / -2.541 | +3.439 / +2.367 | +10.650 / +8.562 | -6.411 / +2.122 | -12.276 / +2.084 |
| NativeAOT / sharedmemory | -0.564 / -2.843 | +0.293 / +4.044 | +1.792 / -4.523 | +1.091 / +3.001 | +3.450 / +3.975 |
| NativeAOT / tcp | -0.152 / -1.427 | -0.168 / +1.145 | -5.010 / -5.559 | -6.322 / +2.318 | -4.967 / -0.241 |

## 6. 如何解释这些正负数

- **JIT PGO ON / TCP Unary 的 RPC/s 配对中位数为 +5.029%。** 它的八项 B2 计数仍全部为 0，且 Unary 不在 pilot eligibility 范围内，所以这个正数不是 B2 grant 达到 ≥5% 目标的证据。它原样保留，不能被选择性计入 eligible 结果。

- A/B 中存在改善也存在回退；这些事实不等于“都只是噪声”，更不等于已定位到某条新增指令。比如 JIT PGO ON / TCP Server 的 A/A 分配变化为两个正数，中位数 +9.606%，说明同一控制 cell 的异步 RPC 分配也有显著过程间变化。应按相同 runtime、transport、shape 查看两轮原 pair；没有把别的 cell 的 A/A 范围搬来作为误差界。

- 八 counter 覆盖 Activated、Disposed、FirstProbeFallbacks、LocalItems、OwnerRefills、ExactSlowAttempts、RecalledGrantBytes、RefundedItemBytes，核对 **1,728 个进程报告计数值均为 0**，区间为 warmup 后到 settled。其中 96 个 B 进程读取真实 internal evidence API；120 个 A 进程本身没有 B2 API，由共同 [diagnostics adapter](https://github.com/SunSi12138/SharpLink/blob/5cabd4d7cc5ce9aa0a87424e36954e414e0912da/test/SharpLink.FlowArchitectureEvidence/GrantEvidenceBridge.cs#L24-L34) 返回 default 零值。未启用结论由源码身份、实际 unsized codec 和 B 的零计数共同支持；不能由 A 的零值或这些有限计数推断未埋点的全局锁、原子指令、队列命令、writer handoff、准备预算峰值或 wire DATA 计数为零。

- 本组没有观察到失败或 NotRun，且每个成功结果同时具备 completed 与 settled 字段。这个有限事实不能填补尚未执行的公平性反例、压力/取消/压缩/blocked Flush、完整矩阵或所有原 PR CI。byte-array 仍是有用的未激活控制；它不能为已否决的 pilot 提供替代性 Go。

## 7. 独立工具与复算方式

[独立 reader](../../../eng/issue735/analysis/independent_b2_byte_array.py) 只用 Python 标准库，不导入主 strict parser，也不解析 `paired-summary.json`。先核原 ZIP 与原文件、Git source/measurement/compiled identity/实际二进制，再从各原报告总量和逐 RPC CSV 重算。每组结果保存为 `independent-verification.json`、`independent-processes.json`、`independent-paired-comparisons.json`、`independent-source-and-binary-manifest.json`、`independent-original-file-inventory.json` 及可读对照表。

下面 `--input` 是包含 `artifact-metadata.json` 和原始 `raw/` 的 population 目录，`--repo` 必须能解析固定 f8、c7、5cab Git 对象；`--output` 可用独立目录。原 ZIP 与 raw 文件只读。

```bash
python3 eng/issue735/analysis/independent_b2_byte_array.py \
  --input /path/to/jit-pgo0-byte-array \
  --output /path/to/audit-jit-pgo0 \
  --repo /path/to/SharpLink \
  --archive /path/to/jit-pgo0-byte-array/rpc-screen.zip
```

JIT PGO ON 要显式选已恢复的 `rpc-screen-original-11715117995.zip`，不能使用保留的不完整 `rpc-screen.zip`。NativeAOT 对应独立 population。输入缺失、失败或 NotRun 不会被填成零；不能靠更换 label 接纳其他负载。

两套 reader 均完成独立推导之后，才用 [交叉检查工具](../../../eng/issue735/analysis/cross_check_b2_byte_array.py) 对比其结果。各 population 的 `independent-versus-strict-cross-check.json` 核对 72 个原报告/CSV hash、八 counter、实际二进制，以及 **2,758 个共享数值**；三组共 8,274 次比较的实际最大差均为 0。它是独立复算完成后的交叉证据，未作为 reader 的统计输入。

```bash
python3 eng/issue735/analysis/cross_check_b2_byte_array.py \
  /path/to/population-containing-both-independent-and-strict-results
```

独立 reader SHA256：`803e9eaaba215f70211e44d539f4e8c5d5532e3082f405bcc08ec515f5a16765`。交叉检查脚本 SHA256：`194886440a6ef472e6804a2bce58e274c0d76347fdbb1744323098baaa2f8db8`。

此次工作只复算、审计并整理原样本，没有修改生产代码、窗口、负载、超时、测试门槛或重新执行性能进程。
