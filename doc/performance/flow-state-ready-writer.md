# #735 / #742：SendPump 单 owner 就绪流调度实传报告

日期：2026-09-24。**本地实验，未推送、未运行新 GitHub CI、未标记 Ready。** 本轮不能核实远端当前 HEAD；基于保存的 `4c17a3892b60496fb31589142010004333b2e891` 源码继续，不声称它仍是远端最新提交。

## 结论

有收益。将信用决定与实际连接 writer 合并、按就绪流调度后，**最终版本在四组 c128 / 16 B / 8 KiB 连接窗口对照中，吞吐中位数增加 48.96%–83.77%，CPU 时间减少 37.25%–44.04%**。每组两个独立进程的吞吐中位数变化均为正，但样本短、仅一台机器，幅度不可外推为稳定生产 SLA。

这不是“独立 B2 信用 owner 发回结果，producer 再进入 SendPump”的旧路径，也不是逐 item Channel 替换 lock。新实验称为 **B3-ready**。两侧均采用相同的有界准备帧环、相同 writer、相同 quantum 和相同刷新策略；**不能与上轮每流只允许一个未完成 emission 的绝对吞吐或 −85% 数字直接混算**。

## 实现与对照

A-ready 保留现有完整 Phase A StreamFlowController，在 producer 上申请信用；B3-ready 把 stream/connection 的信用读写移到实际 SendPump 上。序列化及外部 outbound 扩展仍在 producer 上完成。producer 发布就绪流，writer 按有界 quantum 从该流取独立 StreamData 帧；接收端使用原生产 StreamManager/flow-control，返回真实序列化 WindowUpdate，发送端解析后投递到 writer owner。

四个模式是 A-ready/q1、B3-ready/q1、A-ready/q16、B3-ready/q16。quantum 是调度轮次最多取多少帧，不合并 wire item、不调整协议。所有环均为每流 16 槽；ring 之外每个 producer 还可能持有一个准备中的帧。这里的静态环/身份不回池，并未实现一般 RPC 生命周期。

准备脚本仅对哈希匹配的两份 runtime 源文件施加临时 hook，并复制模板到临时 checkout。最终提交用补丁中 **shipping src/ 差异为空**。测量时确实运行了带 hook 的真实 SendPump，而不是假 writer；没有把 hook 当作已被生产调用链选用的默认实现。

## 主对照：小窗口

c128，每流 2048 items，16 B item；stream window 8192 B，connection window 8192 B，flush 阈值 16384 B；两侧 quantum=16、ring=16。每进程四轮/模式，两个独立进程，八个样本/模式。统计为同格逐模式中位数之比；不是把百分比相加。CPU 是同进程两个端点的总 CPU 时间，因 item 总量相同，其变化也对应 CPU 时间/item 变化。

| 传输 | PGO | A item/s | B3 item/s | 吞吐变化 | CPU 时间变化 | 每个独立进程的吞吐变化 |
|---|---|---:|---:|---:|---:|---|
|sharedmemory|OFF|1,267,290|1,887,754|+48.96%|-37.25%|+32.41%, +59.71%|
|sharedmemory|ON|1,481,830|2,289,813|+54.53%|-40.81%|+53.57%, +41.27%|
|tcp|OFF|1,364,328|2,049,693|+50.23%|-41.47%|+79.99%, +43.69%|
|tcp|ON|1,121,171|2,060,368|+83.77%|-44.04%|+50.46%, +121.33%|

就绪通知加真实 wire 更新投递约 **0.11067–0.12013 次/item**；它是事件频率，不是硬件同步次数。仅信用判断不再有 producer 侧逐 item 全局 gate 或独立信用请求完成。**现有 SendPump 字节预算仍有至少两次 authored RMW/帧**，未消除；CAS 重试、Channel/Lock/runtime 内部原子及正常队列检查没有被伪装成零。

## 大窗口与负向控制

保持 c128/16 B，connection window 524288 B，每流 4096 items，其他条件相同：

| 传输 | PGO | A item/s | B3 item/s | 吞吐变化 | CPU 时间变化 | 每个独立进程的吞吐变化 |
|---|---|---:|---:|---:|---:|---|
|sharedmemory|OFF|2,794,104|3,275,573|+17.23%|-12.52%|+13.28%, +11.85%|
|sharedmemory|ON|2,823,616|3,315,980|+17.44%|-18.97%|+12.16%, +6.46%|
|tcp|OFF|2,850,887|2,724,762|-4.42%|+2.05%|+21.31%, -2.80%|
|tcp|ON|3,699,195|3,667,828|-0.85%|+5.88%|-18.83%, +35.59%|

TCP 的较大窗口结果方向不一致，不能声称普遍提升。4 KiB item 的 quantum=16 控制中，SharedMemory c128/PGO OFF 吞吐 **−12.69%**，TCP c32/PGO ON **−8.13%**；某些其他格子改善。4 KiB 的每帧信用返还不能摊薄，事件频率约 1.008/item。

4 KiB 控制全部 quantum=16 格子实际分配差值为约 **+509 至 +583 B/item**，不满足无分配回归条件。小窗口 tiny-item 减少约 129–162 B/item，但总分配不是零。环缓存/准备帧人口、transport/task/pool 行为包含在过程级 GC 计数内；本轮没有完成其分配栈归因或 retained heap 证明。c1/c8/c32 和所有 q1、q16 行均在完整 summary 中；次级格子只有一个进程，不提升其证据等级。

## 本轮发现并修复的工程问题

1. 首版 writer 在等待凑齐刷新批次，producer 却在等这批数据的信用回传，产生循环等待。改为实验来源无可发送流时先刷新已写批次；正常生产来源的原策略不改。
2. 逐帧轮转将生产接收阈值的跨流信用返还切得更碎。加入最多 16 帧的就绪流调度控制，并给 A 同样的调度条件，避免把不同 batching 配置当作信用优化。
3. 有界 Channel 的空 TryRead/TryPeek 也取内部锁。加入“消息先入队、再发布计数、再唤醒”的协议，writer 仅对已计数消息读 Channel。所有最终样本均验证实际 Channel 读调用数等于 ready + update 消息数，没有空读取被漏算。
4. 首版帧标记使 OwnedFrame 从 40 B 增至 56 B，原有布局测试失败。最终版本复用现有 `_completionState` 槽，使用每流预分配的完成目标，不分配逐帧标记，恢复 40 B。保留原断言；完整最终性能矩阵在修复后重跑，不沿用此前 56 B 结果。

## 两轮自检（实际结果）

第一轮：最终实验 Benchmark、UnitTests、原 B2 研究宿主以及恢复原 runtime 后的默认 Benchmark 构建均 **0 warning / 0 error**。默认 Benchmark whitespace 验证 exit 0。项目引用边界通过；14 项新增 ready-writer Python guards，加上已有 14+7+9 项共 **44 项通过**。新增 workflow 的 6 个 shell 块语法与 YAML 检查通过；该 workflow 没有在 GitHub 执行。

第二轮：**28/28** 新 ready-writer 检查（18 项机制断言 + 10 个真实 Pipe 传输组合）通过。原 B2 模型 **74/74** 通过；原 SendPump progress/layout **14/14**、flow-controller **54/54**、writer boundary **6/6**、wire boundary **10/10** 通过。这些旧控制器/模型测试不转移为 B3 完整生命周期保证。新 100,000 次循环检查的是 capacity completion signal 复用，不是 B3 stream pool generation；旧模型自己的 100k pool checks 另行保留。

**带最终 hook 的完整 UnitTests：1892/1893，exit 2。** `TimedOneWayClientStreamShouldFailAtItsDeadlineWhileTheTransportIsStalled` 失败，断言 accepted Request 应在调用方 deadline 后继续抵达 peer；该路径本轮未改，不声称已证明与 hook 无关或已根因修复。首个全量 wrapper 在工具时限处中断，且记录了旧 56 B 布局失败；未算作完成。一个误写的 focused filter 返回 zero-tests/exit 8，后来使用正确的 PhaseBWireBoundaryTests filter 跑到真实 10 项并 exit 0；未把零测试计成通过。

本地 NativeAOT publish 实际失败于 **NU1100，缺少官方 10.0.11 runtime packs**。本地缓存不足、NuGet DNS 不可达；未运行 NativeAOT 实传，没有用前几轮的 AOT 数据冒充本轮。构建离线使用 NuGetAudit=false，不是线上依赖审计。

## 证据等级与未完成条件

这里是实传 C2S/固定流控制，不是 generated RPC 全链路、完整握手或动态 Duplex。sender 自带协商流控关闭，避免与实验控制器双扣信用；receiver 使用现有生产 flow-control。重复/超额 key-only WindowUpdate 会被 balanced 控制拒绝；退休 key、ABA、全套 abort/deadline、state-capacity/multi-handle 尚未集成。相同准备环及 q16 并不证明一般动态场景下原有全局 waiter FIFO 不变；这里只检查固定就绪流次序、stream-blocked-head skip、债务和 stop 清理。

样本包含 1 秒/至少两轮的 warmup；主样本每进程四轮、跨进程两次，固定四核 .NET 10 JIT PGO ON/OFF。没有硬件 instructions/branches 或 PMU profile，事件数只是直接 authored inventory。没有冷启动一 item stream、完整 waiter 延迟分布或 NativeAOT 验收。生产合并仍需这些条件，不能因为小窗口收益明显而放宽。

## 版本与复现

- 保存的远端源树：`b5061cacb7bfb5a40c2cd282b11d9441e97df7b8`（`4c17a389` archive）。
- 上轮未推送的 transport 基础树：`391cfb790246bbece9635c663a4429a7336a966f`。
- **最终测量完整 tree：`c0ddaea3050842ec39da72d44d0ebc50ea511656`**；没有对应远端 commit。
- SDK 10.0.111 / runtime 10.0.12，Debian 容器，固定 affinity 0–3，DOTNET_PROCESSOR_COUNT=4。环境原有 Platform=linux/amd64；第一次构建的 copy-path 错误用显式 `-p:Platform=AnyCPU` 消除，不修改项目产物协议。

`evidence/final/` 的 **36 报告 / 576 行**全部 status completed、进程 exit 0、校验成功、信用及发布全部结算。`eng/verify-ready-writer.py` 强制完整独立实验人口，拒绝缺失/重复/未知格子、源树/配置不符、假计数、错吞吐算术或失败退出。旧量测版本保存在 `evidence/history/`，不与最终数据混算；其中一处工具调用中断导致空日志/无报告，保留原记录，仅补执行没有结果的 case，没有删除已有失败或不利测量。

解压后运行 `python3 verification/verify-delivery.py` 校验 SHA256 和全部最终报告；加 `--trees` 可离线重建 base、prior、final-intended、final-measured 四个 tree，并检查模板重新生成的 runtime 文件与测量快照一致。不需要 .NET、网络或 GitHub 写入权限。

## 外部参考（原始资料）

- Kestrel `Http2FrameWriter`：https://source.dot.net/Microsoft.AspNetCore.Server.Kestrel.Core/Internal/Http2/Http2FrameWriter.cs.html 。参考按流排队及连接 writer 单一调度的结构，不套用 Kestrel 某个 benchmark 的百分比。
- Netty `DefaultHttp2RemoteFlowController`：https://netty.io/4.2/api/io/netty/handler/codec/http2/DefaultHttp2RemoteFlowController.html 。参考 controller 单线程调用约束，不宣称协议直接兼容。
- .NET `BoundedChannel` 源码：https://source.dot.net/System.Threading.Channels/System/Threading/Channels/BoundedChannel.cs.html 。空读锁的结论来自官方实现，未反汇编本地全部 Channel 内部路径。

**处置：继续 Phase B。既不再把整个方向 No-Go，也不把本轮固定生命周期收益当作 Ready for Review。**
