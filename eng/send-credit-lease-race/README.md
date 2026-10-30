# Send-credit lease 捕获竞态回归

`AcquireSendCreditAsync` 必须在取得 `SendState` 的同一个 `_gate` 临界区内捕获它的 lease。旧版本在释放 gate 后准备 contended-call 参数时才读取 `state.Lease`；此间另一线程可以完成旧流、将对象回池，并在同 key 上租用同一个对象。对象引用与刚读出的 lease 都指向新 generation，旧调用因而错误通过身份检查并消耗新流信用。

本分支的生产修复只将 lease 捕获移入原 gate，然后把局部值传给原 contended 方法。它不修改 pool、FIFO、WindowUpdate、ReturnUnsentCredit、默认窗口、公共 API 或 wire protocol。

## 可重复的交错

测试调用真实的 public keyed API，在原 probe 释放 gate、contended 参数求值之前暂停。控制线程依次归还旧流信用、完成旧流、同 key 创建新流，再释放暂停线程。断言先确认确实复用了同一个 pooled object 且 lease 增加。

测试 fixture 的 stream/connection window 为 16/32 B。新流先占 8 B，应该保持 stream/connection credit 为 8/24；旧调用必须被拒绝。旧 f8 实际错误接受该调用，使信用变成 0/16。fixture 的小窗口用于确定性地进入竞争窗口，不更改生产默认值。

相同 C# 文件还保留七个控制：不同 key 复用、同 generation 正常归还、复用后取消的原 token、连接终止原异常、未复用的流完成、stream abort 原异常、以及 tombstone 容量释放后原本无 state 的调用。所有操作走真实公开入口，不反射调用私有 contended 方法、不修改池。阻塞回调使用独立 LongRunning 测试任务，所有等待保持原 5 秒界限。

## 两臂都只注入相同诊断代码

`apply.py` 只有一个 `instrument()` 转换，**不提供任何算法修复**：

- 插入一个 gate 外的 instance callback。
- 插入一个在 `_gate` 内读取 object identity、lease、credit 的只读 snapshot。
- 复制同一份 8-case 测试。

移除上述两个代码段后，整个 production input 必须逐字节恢复。工具没有 `fixed` 转换、lease 赋值重写或 production source 替换。若以后撤销产品修复，current 臂应重新失败；工具不能替它变绿。

`old` 臂要求提交固定为 `f8ba89121c87a6410a0139c44d7db30eab877f41`，并验证 controller SHA256 `7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2`。`current` 臂直接 checkout 工作流实际 HEAD，只接受未改动的 committed input，不用固定 production SHA allowlist；因此未来生产修改会被实际测试。调用边界或诊断锚点变化时工具拒绝注入，必须评审新的观测位置。完整 current production diff、Git tree、注入 patch 和 source/test/tool hashes 均存入 artifact。

## 执行和通过条件

在仓库根目录、已安装仓库指定 SDK 且设置了可写 `RUNNER_TEMP` 的环境运行：

```bash
python3 eng/send-credit-lease-race/run.py --out artifacts/send-credit-lease-race
```

运行器创建两个独立 detached worktree，不向当前 checkout 注入诊断代码。它测试的是 HEAD 已提交的产品；本地未提交修复不能被误认成已测代码。

| 臂 | 固定计划 | 通过条件 |
|---|---|---|
| old | focused 一轮、8 cases | exit 2，精确 1 failed / 7 succeeded / 0 skipped；必须是同 key 同对象新 lease 被接受且信用为 0/16，任意其他失败不算有效 red。 |
| current | focused 四轮 | 每轮 8/8、exit 0、0 skipped。 |
| current | unfiltered 完整套件四轮 | 每轮 2,482/2,482、exit 0、0 skipped；原测试和超时不变。 |

`FULL_COUNT=2482` 对应源码逐case推导的 2,474 ordinary + 8 原有注入 controls。下方明确列出每层增量，无移除、无runtime发现/通过声明。历史完整执行证据保留原始计数；focused/old-negative、并行、超时和失败标准均不改。

每次运行保存真实 argv、cwd、退出码、stdout/stderr、完整 TUnit 输出、输入/输出源码身份和编译后二进制 hashes。每轮前后再次核对全部 DLL/PDB/config 身份，避免测到另一套 build；下一轮前只清理隔离目录中已经归档的旧 TestResults，防止旧 HTML 被当成新报告。非预期失败立即使工作流失败，不重跑到绿。artifact 上传使用 `if: always()`，失败日志照常保留。

## 已有实际 red/green 证据与本工作流边界

[run 38166410894](https://github.com/SunSi12138/SharpLink/actions/runs/38166410894)、[artifact 11712012693](https://github.com/SunSi12138/SharpLink/actions/runs/38166410894/artifacts/11712012693) 已实际编译并运行相同 8 个测试：旧 f8 为精确 1/8 red，修复臂四轮 focused 全过，四轮完整套件均 2,090/2,090。原 artifact 2,047,595 B，SHA256 `e5dc1b0ae73bffc11f7f19f98ed0161bea5eaef71ab9486f98f46d3298b7811b`，harness commit 为 `d421334ed1069c276ff86113a4b436399900f070`。

该历史 run 的修复臂由诊断 overlay 提供算法变化，用于归因；**本持久回归取消了这项能力，改为测试 current 的真实生产源码**。新 workflow 的执行结果应以其自己的 run/artifact 为准，不能借历史 green 声称新 workflow 已通过。此目录只覆盖 lease 捕获的 correctness，不提供 B2/B3、吞吐或 NativeAOT 性能结论。

## Combined correctness source census

The exact ordinary expectation is **2,474**: dev `57f06f17` **2,188** + required
physical ownership **111** + cooperative maintenance **157** + static-stream
flags **6** + send-pump cleanup **6** + shutdown composition **5** + queued-owner
partial-prefix variant **1**. The original zero-prefix ownership case remains covered.
The five shutdown-composition cases are three runtime output-safety/reentry cases
and two server graceful-budget cases.
No case declarations are removed. Historical #931's 113 includes two queued-reader
cases already on dev; the five preexisting Explicit workers remain outside ordinary
execution. The lease runner alone adds its unchanged eight injected cases (**2,482**).

This expectation comes from source path/method/Arguments identities, not a convenient
run result. It is **not** final-head runtime discovery, execution or performance
acceptance. Fresh authorized execution must confirm the exact identity set with zero
drops/skips; old-negative and focused counts, markers, timeouts, parallelism and
acceptance rules are unchanged.
