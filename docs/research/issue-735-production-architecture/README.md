# Issue #735：生产架构研究交付索引

研究日期：2026-10-11 UTC。工作开始时重新确认的基线为 [`dev@f8ba89121c87a6410a0139c44d7db30eab877f41`](https://github.com/SunSi12138/SharpLink/commit/f8ba89121c87a6410a0139c44d7db30eab877f41)，已包含 #840、#841；后续正确性和内存优化不能计入 B3 收益。

**最终阶段决定：当前 B2 pilot 与 D 方向锁分离均 No-Go，旧 #803 不恢复；三项基线正确性缺陷独立提交。** B2 的四文件生产接入已实际编译，完成确定性 red/green、多轮完整测试、JIT/NativeAOT 分配实验与真实生成式 RPC。最终 RPC 原计划 648 个位置中，457 成功、2 Failed、189 NotRun；两个 JIT DTO 任务在真实发送队列容量拒绝处停止。NativeAOT DTO 完整 144 进程通过，但 12 个 eligible cell 无吞吐中位数达到 5%；两个被截断的 JIT 各 9 个 eligible cell 也没有达到该值，且缺少计划中的 A/A。局部 grant 明确启用、owner admission 大幅减少，仍没有支持扩大接入的整体收益。新 B3/混合方案停留在源码与成本评审，不宣称其所有可能实现已被实测证伪，也不宣称完成了全生产矩阵。

## 先读历史失败，再读候选和实际测量

| 文档 | 内容和主要结论 |
|---|---|
| [历史失败与原始证据](failures-and-evidence.zh-CN.md) | Phase A、B0/B1/B2/B3 演化、#800/#803 的实际实现、真实 RPC TCP −31.18% / SharedMemory −32.82%、八轮旧测试三轮失败、本次两版确定性 red/green 与固定 H/G 八轮控制。已证实、合理怀疑、未测分开；原 CI 红灯不删除。 |
| [成熟框架与 A/B/C/D 评审](frameworks-and-candidates.zh-CN.md) | Netty、Kestrel、MsQuic、gRPC-Go、Rust h2/tonic 的固定源码、窗口 owner、批处理、取消与阻塞 Flush。候选信用守恒、生命周期、操作/空间成本及迁移边界。串行模型不当作生产 RPC。 |
| [独立正确性修复与完整失败账](correctness-findings.zh-CN.md) | #849 lease ABA、#850 snapshot ownership、#851 terminal credit；旧版指定 red、修复后的原 CI 与多轮测试、固定 40 次控制、仍未定位的 24,624 B 失败以及 B2 实际 deadline red。 |
| [D：最终 No-Go、架构与测量方法](directional-control-and-methods.zh-CN.md) | 最小 send/receive gate 分离、正确性控制、两版各 432 个真实 generated RPC 性能进程、completed/settled 边界、48 B/controller 分配实测、实际本地 NativeAOT RPC 执行的部分成功与环境失败。 |
| [D：独立完整数据复核](directional-independent-review.zh-CN.md) | V1/V2 分别给出三运行时全部 48 cell、原四对、A/A、正信号、负 outlier、CPU/分配/quantile 与统计边界。独立数值复算，不调用主分析器，不合并成八对。 |
| [B2：实际 pilot 架构和分配](b2-pilot-architecture.zh-CN.md) | 四个生产文件、唯一信用 authority、lease/generation/回池、逐路径静态锁与队列次数、已执行的 +112 B/new generation 和零分配稳态，以及资源边界。 |
| [B2：真实 RPC 方法与结果](b2-real-rpc-methods-and-results.zh-CN.md) | c7 共同正确性 control、sized DTO 与 byte-array 的实际启用检查、计划 648 个进程、每轮原始失败与独立结果；不把计划或编译标记当成执行。 |
| [B2：sized DTO 完整独立复核](b2-sized-dto-independent-review.zh-CN.md) | 两个 JIT 的所有部分 pair、失败及 NotRun；NativeAOT 的全部四对、A/A、CPU/分配/尾延迟与真实 activation。 |
| [B2：byte-array 控制完整复核](b2-byte-array-control-review.zh-CN.md) | 216 成功进程、八项 counter 全零，所有正负数据只作为控制，不当成 B2 grant 收益。 |
| [B2：发送队列容量归因](b2-queue-capacity-attribution.zh-CN.md) | 两次真实资源拒绝、payload 与 frame 单位、上限拒绝语义、源码所有权与未保存的结清现场分开。 |
| [B2：机器可读证据索引](b2-artifact-index.json) | 六次阶段构建/正确性运行、两轮 footprint、两轮 RPC 所有原 artifact、失败及本地不完整 ZIP 的保留与恢复。 |
| [D：机器可读证据索引](directional-artifact-index.json) | 14 个原 artifact 的 run、固定 source、文件大小、GitHub/本地 ZIP SHA256；六个 RPC runtime population 的 plan/ELF/DLL 身份；V1 两个本地 ZIP 不完整的恢复与逐条一致性记录。 |
| [离线重算工具说明](../../../eng/issue735/analysis/README.md) | 历史 Phase A/B、#803、matched transport、D 与 B2 的独立原样本重算及完整证据恢复命令、输入目录和 provenance。重算不是新测量。 |

历史审计最重要的三条失败链是：研究原型按批摊销的工作在 #803 又变为逐 item 多层协调；terminal 再次按 key 打开状态导致原异常被通用 closed 异常覆盖；取消信号完成与 prepared-buffer 清理结算之间缺少可靠完成关系。操作数下界、确定性因果实验和不能量化的部分都在第一份报告中，不把推断写成实测占比。

## 本次实际证据如何计数

- D V1：[run 38164814650](https://github.com/SunSi12138/SharpLink/actions/runs/38164814650)，JIT PGO OFF/ON、实际 NativeAOT 各 144 个进程，合计 432。
- D V2：[run 38165237528](https://github.com/SunSi12138/SharpLink/actions/runs/38165237528)，另一个 measurement source 的同样 432 个进程。两版合计 **864 次执行**，保持两个独立测量群体；不构造跨版本八对、不跨 runner 合并绝对吞吐。
- 48 B 增量来自[独立 footprint run 38165866902](https://github.com/SunSi12138/SharpLink/actions/runs/38165866902)，是 constructor caller-thread managed allocation，不能换算成 RPC 分配收益。首次[编译失败 38165692785](https://github.com/SunSi12138/SharpLink/actions/runs/38165692785) 保留。
- 本地另实际运行 V1 Native ELF：TCP A/B 各 256 个 RPC、exit 0；SharedMemory A 在 socket 创建时 exit 1，未开始测量，B 未执行。它不进入前述 864 或任何性能统计，完整原命令和 partial files 保留在档案 `evidence/new/38164814650/nativeaot/local-execution/`。
- #803 的原 microkernel、matched transport、托管 generated RPC、历史真实 NativeAOT 和 smoke 五种证据分别登记，不能互相补齐缺失 gate。

## 独立正确性修复与 B2 阶段边界

三项独立正确性 PR 分别为 [#849](https://github.com/SunSi12138/SharpLink/pull/849)、[#850](https://github.com/SunSi12138/SharpLink/pull/850)、[#851](https://github.com/SunSi12138/SharpLink/pull/851)。每项最初均以 Draft 从固定 dev 隔离创建，包含真实 old-red/current-green 和原 CI 结果；交付复核时 #849/#851 仍为 Draft，仓库记录 #850 于 2026-10-11 21:10:48 UTC 转为 Ready for review，三个 head 均未变化、均未合并。详细固定源码、merge SHA、artifact、失败保留及验证边界见正确性报告。其中 #850 的早期 24,624 B 分配断言失败仍未定位，后续固定 40 次对照均通过不消除原失败。

后续 B2 真实性能的共同正确性 control 固定为 [`c7ba1768`](https://github.com/SunSi12138/SharpLink/commit/c7ba1768f31ce7219dc438dc6009347de6806ed5)。它相对初始 dev 只组合上述三项修复，没有 D/grant/SendPump 改动；[独立共同回归](https://github.com/SunSi12138/SharpLink/actions/runs/38171029907) 已实际旧 3 red、当前四轮 focused 3 和四轮 full 2085 通过。lease 的八项 hook 回归仍单独登记，不冒充共同 focused 3 的内容。

B2 pilot 保留在独立 [研究分支](https://github.com/SunSi12138/SharpLink/tree/research/735-b2-server-grant-pilot-20261011)，不作为待生产合并的性能 PR。完整阶段顺序：

- [`4f564d31`](https://github.com/SunSi12138/SharpLink/commit/4f564d310ccab30faba7642a8fd1109b98b31700) 的首轮构建被 fixture int/uint 类型错误挡住；[run 38170726573](https://github.com/SunSi12138/SharpLink/actions/runs/38170726573) 保留，未当作竞态 red。
- [`d472eee7`](https://github.com/SunSi12138/SharpLink/commit/d472eee7cb2097c33342b20df4f394269f864824) 的 [run 38171089212](https://github.com/SunSi12138/SharpLink/actions/runs/38171089212) 实际编译，41 focused 中 40 pass/1 fail；deadline late refund 为 F=0/D=64/G=0，后续所有 full 检查 NotRun。
- [`ba9dfedc`](https://github.com/SunSi12138/SharpLink/commit/ba9dfedc3a769a14f03cb5c522f4921f5da655eb) 只纳入独立 terminal-settlement bool 修复。相同 deadline 断言通过；[run 38171674863](https://github.com/SunSi12138/SharpLink/actions/runs/38171674863) 的 focused41、四轮 full2123、generator271、load77、integration454 均通过，随后原 whitespace gate 失败，整轮仍为红，maintainability/mutant 尚未运行。
- [`754d723c`](https://github.com/SunSi12138/SharpLink/commit/754d723c7866a67a48f3952495d8aeda3159d07b) 仅按该格式错误将线程 initializer 前一空格改为换行。四个生产文件逐字节保持 ba9；[run 38172137769](https://github.com/SunSi12138/SharpLink/actions/runs/38172137769) 实际 25/25 staged gate 通过，包括原样例的指定 mutant red 与 Dispose control。
- [`60828816`](https://github.com/SunSi12138/SharpLink/commit/608288167436ce361920e7ee41fa9bb958229707) 新增测量 harness，生产字节仍相同；[run 38172711056](https://github.com/SunSi12138/SharpLink/actions/runs/38172711056) 同 SHA 的 25 gate 与 footprint 成功，六 RPC job 统一在 build-A 的 CS8604 失败。没有 RPC / codec preflight，648 个测量位置均 NotRun。
- [`5cabd4d7`](https://github.com/SunSi12138/SharpLink/commit/5cabd4d7cc5ce9aa0a87424e36954e414e0912da) 只把新 helper 的 Validate 参数声明成 nullable，原 null 拒绝不变；[run 38173700315](https://github.com/SunSi12138/SharpLink/actions/runs/38173700315) 独立重新执行所有准入与测量：25 项 staged gate 与 footprint 成功，真实 RPC 为 457 成功 / 2 Failed / 189 NotRun；两个 JIT DTO 的容量拒绝保留，整轮为 failure，不覆盖 608 失败。

现有 byte-array 生成 RPC 的 codec 不能证明这次 server-sized B2 已启用。新测量将 byte-array fallback 与正常 source-generated exact-size DTO 分开，A/B 每臂完全相同，并在实际 codec preflight 和生产路径计数中拒绝静默回退。不同 payload、runtime、measurement source 的结果分别登记；三个 byte-array population 实际全部八计数为零，不能从它们的性能百分比报告 B2 收益。sized DTO 的 1000×16 RPC 实际有 995 次 local、4 次 refill 和 1 次 first exact，但不计为 SendPump 协调的减少。

上述 25 项是 pilot 的阶段准入，包括全部原测试套件及若干工程 gate，但不替代原 PR 的独立 allocation-gate、Package Smoke、CodeQL。报告 PR [#852](https://github.com/SunSi12138/SharpLink/pull/852) 的 [原 PR Fast run 38172281638](https://github.com/SunSi12138/SharpLink/actions/runs/38172281638) 中 fast job 成功、allocation-gate 因 SharedMemory 5 样本 spread=59.848 B/op 超过 50 B/op 而失败；median=1325.368 B/op 低于 1450 上限。该分支 src 未变也不能把红灯认定为无关噪声。它与 #850 的 24,624 B/0 断言失败分别保留，原因尚未归属。

## 可复现交付范围

文档与小型分析脚本纳入研究分支；原 ZIP、二进制、逐 RPC CSV、完整 TUnit/CI 日志与失败样本放在 Actions artifact 和 evidence archive，不将大 ZIP 提交到 Git。索引中的 `evidence/...` 是档案解压根的相对路径，不要求固定本机目录。

没有删改原 CI、放宽旧测试断言/超时，或为了取得收益调大窗口、socket、queue、Flush。B2 的 189 个 NotRun、完整取消/拥塞/压缩/异构流性能矩阵、Q 敏感性、锁/原子/唤醒归因与新架构生产验收仍明确未完成。按事先止损规则，本轮最小真实 RPC 没有给出足够整体收益，停止扩大实现与矩阵；不调低负载或提高资源上限来“救活”结果。静态公平性缺口和未捕获的失败后资源余额仍作为 Go 阻断，不以绿色测试推断不存在。
