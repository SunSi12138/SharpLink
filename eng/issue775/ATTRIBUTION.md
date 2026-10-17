# #775 静态路径归因（不是第二阶段实现）

本补丁仅增加诊断脚本、只读 Actions 工作流和独立诊断程序。不会修改 `src/`、
原始 benchmark、基准 runner、预算或任何业务协议。#862 保持 Draft，#775 不关闭。

## 固定输入与事先确定的实验

A = `1a54993d3550911f5979a7b4d3f9f5f63ee86a64` 原始 dev。
B = `d2cb248b5e4b573d448092f7f152c2f2dd281e30` 开启 `SharpLinkStaticBindingPrototype`。
C = 同一个 d2cb248b 源码，关闭实验开关；C 是归因对照，不能替代 A 做验收。
诊断驱动的提交 SHA 单独记录，不冒充被测源码 SHA。

同一 timing job/VM 串行执行，启动前冻结计划：

1. A/A 前半段 `X Y Y X`，X/Y 使用 **同一个 A 目录和所有 DLL**，不是分别重新构建。
2. 三臂六块 `ABC / BCA / CAB / CBA / ACB / BAC`，每臂在每个位置出现两次。
3. A/A 后半段 `Y X X Y`。两个半段之间隔着三臂矩阵；不将 A/A 当作可相减的噪声。

三臂每个 slot：TCP/SharedMemory 的 Unary 分别测 c1/p1、c1/p4、c8/p1、c8/p4；
OneWay 保留 c1/p1、c8/p4。A/A 每 slot 测 TCP Unary c8/p4、TCP OneWay c1/p1。
所有 slot 都保留原始 BDN `UnaryBenchmarks.Rpc_OnewayPublish`：
3 launches × 6 warmup × 15 actual iterations，invocationCount=4096。
PayloadSize=16/256 是原有 setup 标签，不代表不同实际 OneWay payload。

合计 26 个 slot、232 次真实传输执行、2,340 次 BDN actual measurement。
沿用每进程预热 10,000、测量 100,000 次，两个 CPU、ProcessorCount=2、原 tiering/PGO。
无采样器进入 timing。所有编译服务在测量阶段开始前关闭。
A 只叠加既有 run.py 白名单中的五个 test harness 文件，不能改生产代码。
三臂记录 SDK、runtime、CPU、构建输出 DLL SHA256、overlay 和共有输入校验值。
A/A 使用相同文件，结束后再核对全部被测 DLL 未改变。

每臂在原 CPU/环境运行一次原始 allocation gate；B 再跑 +512 B/op 负对照。
保留四项原 median/spread 预算，无重定基线或重试挑数。
归因门禁不能替代原始完整 ABBA/BAAB 验收。

## 采样及 JIT（另一个 VM，不参与计时比较）

独立 profiles job 在自身 VM 上顺序构建/分析 A、B、C；不可比较两个 job 的绝对时间。
固定 `dotnet-trace 9.0.661903`，不因失败而回退版本，记录工具版本和配置。
收集线程栈及 GC、JIT、contention、ThreadPool 等运行时事件：

- TCP Unary c8/p4 与 SharedMemory Unary c8/p4，既有 harness，诊断执行 300,000 次。
- 原始 OneWay 方法由独立 `AttributionProbe` 直接调用，warmup 10,000 后执行 1,000,000 次。
  Probe 标记 warmup/steady/cleanup；它只用于调用栈归因，**不是 BDN 分数或业务接收完成证明**。

保留原始 `.nettrace`、inclusive/exclusive topN、Speedscope 和每步退出码。
RPC trace 包含启动、Prepare、warmup、GC、业务及输出序列化，不能将总栈占比称为稳态 CPU 百分比。
EventPipe SampleProfiler 是线程栈/壁钟时间采样，不是精确 on-CPU 采样。
运行时事件保留在原始 nettrace；Speedscope 不是其无损替代。

另启无 EventPipe 的进程输出 JIT 反汇编，保留真实 tiering/PGO，不禁用内联来制造热点。
限定 Client/Server 中 Invoke/GetReady/Resolve/MoveNext；保留可见 Tier0/Tier1 版本。
仅出现 Tier0 或某函数被内联而没有独立 listing 时，不得当作完整优化代码证据。
优先审阅调用控制结构复制、连接选择/admission、服务端绑定/方法查找三类候选开销。
未采集到的硬件 CPU 栈、调度切换和内联决策不声称已经确认。

官方工具说明：
https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace
https://www.nuget.org/packages/dotnet-trace/9.0.661903

## 运行与判读

```sh
python3 -m unittest discover -s eng/issue775 -p test_attribution.py -v
# A/B/C 是三个干净 worktree；脚本拒绝混合输出、错误 SHA 或残留诊断环境。
python3 eng/issue775/attribution.py --driver "$PWD" --work /path/to/worktrees \
  --mode timing --output /path/to/new-output
python3 eng/issue775/attribution_report.py /path/to/new-output
# profiles 使用另一组干净 worktree 和新的输出目录。
```

工作流 `issue-775-attribution.yml` 可手动运行；隔离分支上含 `[issue775-attribution]` 的显式提交也会触发。
只读权限，失败保留 artifact，不自动提交、合并、重跑、调预算或授权第二阶段。

报告分别给出 B/A、C/A、B/C 和 Y/X，业务完成吞吐、sender/receiver p50/p99/p99.9、CPU、分配、drain 与 BDN 原始层级。
配对按三臂块（六对，df=5）和 A/A 相邻对（四对，df=3）完成。
95% log-t 区间是探索性的，假定配对近似独立且 log-ratio 近似正态，未多重比较校正。
同时显示中位数比与配对几何比区间，两者不是同一个统计量；不显著不证明等价。
交叉组合帮助区分并发与池大小的关联，不自动证明哪个函数是因果根源。

没有批准的等价边界，当前结果只能指导下一次单点实验。任何生产优化仍需固定源码、
完整安全/回归测试、原基线 ABBA/BAAB、既有分配门禁与负对照，不能用诊断成绩放行。
