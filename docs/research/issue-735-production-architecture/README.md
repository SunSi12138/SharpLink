# Issue #735：生产架构研究交付索引

研究日期：2026-10-11 UTC。工作开始时重新确认的基线为 [`dev@f8ba89121c87a6410a0139c44d7db30eab877f41`](https://github.com/SunSi12138/SharpLink/commit/f8ba89121c87a6410a0139c44d7db30eab877f41)，已包含 #840、#841；后续正确性和内存优化不能计入 B3 收益。

**已能作出的决定：旧 #803 不恢复，当前 D 方向锁分离不晋级生产性能优化，三项基线正确性缺陷独立提交。** 改进 B2 的生产 pilot 已开始实际编译、确定性与完整测试；此前的 deadline 信用归还 red 已用同一测试验证修复，格式门槛仍需通过。B2 尚无真实性能结果，B3/混合方案也没有完成新生产实现验收。因此本目录不是“所有候选均已实测 No-Go”的声明，也不是完整生产 Go。

## 先读历史失败，再读候选和实际测量

| 文档 | 内容和主要结论 |
|---|---|
| [历史失败与原始证据](failures-and-evidence.zh-CN.md) | Phase A、B0/B1/B2/B3 演化、#800/#803 的实际实现、真实 RPC TCP −31.18% / SharedMemory −32.82%、八轮旧测试三轮失败、本次两版确定性 red/green 与固定 H/G 八轮控制。已证实、合理怀疑、未测分开；原 CI 红灯不删除。 |
| [成熟框架与 A/B/C/D 评审](frameworks-and-candidates.zh-CN.md) | Netty、Kestrel、MsQuic、gRPC-Go、Rust h2/tonic 的固定源码、窗口 owner、批处理、取消与阻塞 Flush。候选信用守恒、生命周期、操作/空间成本及迁移边界。串行模型不当作生产 RPC。 |
| [独立正确性修复与完整失败账](correctness-findings.zh-CN.md) | #849 lease ABA、#850 snapshot ownership、#851 terminal credit；旧版指定 red、修复后的原 CI 与多轮测试、固定 40 次控制、仍未定位的 24,624 B 失败以及 B2 实际 deadline red。 |
| [D：最终 No-Go、架构与测量方法](directional-control-and-methods.zh-CN.md) | 最小 send/receive gate 分离、正确性控制、两版各 432 个真实 generated RPC 性能进程、completed/settled 边界、48 B/controller 分配实测、实际本地 NativeAOT RPC 执行的部分成功与环境失败。 |
| [D：独立完整数据复核](directional-independent-review.zh-CN.md) | V1/V2 分别给出三运行时全部 48 cell、原四对、A/A、正信号、负 outlier、CPU/分配/quantile 与统计边界。独立数值复算，不调用主分析器，不合并成八对。 |
| [D：机器可读证据索引](directional-artifact-index.json) | 14 个原 artifact 的 run、固定 source、文件大小、GitHub/本地 ZIP SHA256；六个 RPC runtime population 的 plan/ELF/DLL 身份；V1 两个本地 ZIP 不完整的恢复与逐条一致性记录。 |
| [离线重算工具说明](../../../eng/issue735/analysis/README.md) | 历史 Phase A/B、#803、matched transport 和新 D 的 Python 标准库重算命令、输入目录与 provenance。重算不是新测量。 |

历史审计最重要的三条失败链是：研究原型按批摊销的工作在 #803 又变为逐 item 多层协调；terminal 再次按 key 打开状态导致原异常被通用 closed 异常覆盖；取消信号完成与 prepared-buffer 清理结算之间缺少可靠完成关系。操作数下界、确定性因果实验和不能量化的部分都在第一份报告中，不把推断写成实测占比。

## 本次实际证据如何计数

- D V1：[run 38164814650](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650)，JIT PGO OFF/ON、实际 NativeAOT 各 144 个进程，合计 432。
- D V2：[run 38165237528](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528)，另一个 measurement source 的同样 432 个进程。两版合计 **864 次执行**，保持两个独立测量群体；不构造跨版本八对、不跨 runner 合并绝对吞吐。
- 48 B 增量来自[独立 footprint run 38165866902](https://github.com/SunSi12138/SharpLink/actions/runs/38165866902)，是 constructor caller-thread managed allocation，不能换算成 RPC 分配收益。首次[编译失败 38165692785](https://github.com/SunSi12138/SharpLink/actions/runs/38165692785) 保留。
- 本地另实际运行 V1 Native ELF：TCP A/B 各 256 个 RPC、exit 0；SharedMemory A 在 socket 创建时 exit 1，未开始测量，B 未执行。它不进入前述 864 或任何性能统计，完整原命令和 partial files 保留在档案 `evidence/new/38164814650/nativeaot/local-execution/`。
- #803 的原 microkernel、matched transport、托管 generated RPC、历史真实 NativeAOT 和 smoke 五种证据分别登记，不能互相补齐缺失 gate。

## 独立正确性修复与 B2 阶段边界

三项独立 Draft PR 分别为 [#849](https://github.com/SunSi12138/SharpLink/pull/849)、[#850](https://github.com/SunSi12138/SharpLink/pull/850)、[#851](https://github.com/SunSi12138/SharpLink/pull/851)。每项都从固定 dev 隔离，包含真实 old-red/current-green 和原 CI 结果；未自动合并。详细固定源码、merge SHA、artifact、失败保留及验证边界见正确性报告。其中 #850 的早期 24,624 B 分配断言失败仍未定位，后续固定 40 次对照均通过不消除原失败。

后续 B2 真实性能的共同正确性 control 固定为 [`c7ba1768`](https://github.com/SunSi12138/SharpLink/commit/c7ba1768f31ce7219dc438dc6009347de6806ed5)。它相对初始 dev 只组合上述三项修复，没有 D/grant/SendPump 改动；[独立共同回归](https://github.com/SunSi12138/SharpLink/actions/runs/38171029907) 已实际旧 3 red、当前四轮 focused 3 和四轮 full 2085 通过。lease 的八项 hook 回归仍单独登记，不冒充共同 focused 3 的内容。

B2 pilot 位于独立 [研究分支](https://github.com/SunSi12138/SharpLink/tree/research/735-b2-server-grant-pilot-20261011)。截至此索引阶段：

- [`4f564d31`](https://github.com/SunSi12138/SharpLink/commit/4f564d310ccab30faba7642a8fd1109b98b31700) 的首轮构建被 fixture int/uint 类型错误挡住；[run 38170726573](https://github.com/SunSi12138/SharpLink/actions/runs/38170726573) 保留，未当作竞态 red。
- [`d472eee7`](https://github.com/SunSi12138/SharpLink/commit/d472eee7cb2097c33342b20df4f394269f864824) 的 [run 38171089212](https://github.com/SunSi12138/SharpLink/actions/runs/38171089212) 实际编译，41 focused 中 40 pass/1 fail；deadline late refund 为 F=0/D=64/G=0，后续所有 full 检查 NotRun。
- [`ba9dfedc`](https://github.com/SunSi12138/SharpLink/commit/ba9dfedc3a769a14f03cb5c522f4921f5da655eb) 只纳入独立 terminal-settlement bool 修复。相同 deadline 断言通过；[run 38171674863](https://github.com/SunSi12138/SharpLink/actions/runs/38171674863) 的 focused41、四轮 full2123、generator271、load77、integration454 均通过，随后原 whitespace gate 失败，整轮仍为红，maintainability/mutant 尚未运行。
- [`754d723c`](https://github.com/SunSi12138/SharpLink/commit/754d723c7866a67a48f3952495d8aeda3159d07b) 仅按该格式错误将线程 initializer 前一空格改为换行。四个生产文件逐字节保持 ba9；所有原门槛需重新实际通过后才开始性能。

现有 byte-array 生成 RPC 的 codec 不能证明这次 server-sized B2 已启用。准备中的新测量将 byte-array fallback 与正常 source-generated exact-size DTO 分开，A/B 每臂完全相同，并在实际 codec preflight 和生产路径计数中拒绝静默回退。尚未执行的性能结果不能填入收益表。

## 可复现交付范围

文档与小型分析脚本纳入研究分支；原 ZIP、二进制、逐 RPC CSV、完整 TUnit/CI 日志与失败样本放在 Actions artifact 和 evidence archive，不将大 ZIP 提交到 Git。索引中的 `evidence/...` 是档案解压根的相对路径，不要求固定本机目录。

没有删改原 CI、放宽旧测试断言/超时，或为了取得收益调大窗口、socket、queue、Flush。B2 的未执行性能部分、完整取消/拥塞/压缩/异构流性能矩阵和新架构生产验收仍明确未完成。固定共同正确性 SHA 后先执行最小真实 RPC 对照，只有正确性与整体成本同时有正面证据，才扩展生产接入。
