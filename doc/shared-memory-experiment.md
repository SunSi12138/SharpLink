# 共享内存传输实验报告

## 当前结论

本分支实现已在 macOS arm64、.NET SDK 10.0.102 / runtime 10.0.2 上完成 Release JIT、独立进程 NativeAOT、包消费和短时 Chaos 正确性验证。Windows x64 与 Linux x64 目前只有 framework-dependent publish 构建检查；两平台运行时测试、两平台 NativeAOT、正式性能矩阵、2 小时 nightly 和 24 小时最终 soak 尚未执行。因此当前状态是“macOS arm64 正确性实验通过，其余门禁待验证”，不得进入正式支持矩阵、合并或发布。

本轮主动暂停性能判定：采样期间发现同机另一个仓库正在运行 24 小时 Chaos，已采集的 LoadTest 与 trace 会受到资源竞争影响。相关文件已移至本地忽略目录 `artifacts/invalid/background-chaos-20260718-*`，不得用于任何吞吐、延迟、分配或平台特化结论。待环境干净后必须从头重跑，不复用这些样本。

不满足任一正式门槛时应保留本分支和原始 JSON，标记实验未通过；不得通过放宽阈值、忽略错误或静默降级获得通过结论。

## 设计与安全边界

- 公共入口为 `UseSharedMemory(name, configure)`、`SharedMemoryClientTransportFactory` 和 `SharedMemoryServerTransportListener`；Protocol v2 与第三方传输接口未修改。
- 命名管道使用 `CurrentUserOnly`，承载有界握手、资源描述、合并唤醒、关闭和存活检测。RPC 数据只经过每连接双向共享内存环。
- 映射为 4 KiB 固定头部加 Client→Server、Server→Client 两个等容量环。头部包含 magic、布局版本、容量、nonce、隔离游标、等待标志和关闭状态。
- 客户端只接受用户私有临时目录内、GUID 命名、`.shm` 后缀、非符号链接的映射；Unix 权限不得包含 group/other 位。
- 双方容量不一致取较小值；SpinCount 仅影响本端。失败直接返回结构化错误，不回退到 TCP、UDS 或 Pipe。
- 通知后端为 `named-pipe-control`。双方使用“登记等待后重新检查”协议，只有实际等待者才触发控制通知；data/space 使用 bitmask 合并写，进程内 waiter 使用可复用 ValueTask source。控制语义变更由共享内存握手版本 2 隔离，旧版本会在映射前失败。
- 未知控制 bit 会作为 `ProtocolViolation` 暴露，不会被吞成普通断连；spill 总量受 256 MiB 上限约束，越界在分配前返回 `ResourceExhausted`。入站 staging 与出站累积 spill 使用池化 sequence segments，不在增长时复制已有字节。

## 正确性优先的热路径改造

- 默认证据采集只启用容量、后端、spill/wait/notification；`--detailed-shm-evidence` 才启用 direct、spill 原因与复制、staging、通知请求/合并和游标刷新，避免正式计时被高频观测扰动。
- reader 关闭不再用 `Task.Yield` 轮询 outstanding read，而是等待一次性完成信号；映射仍在 `AdvanceTo` 前保持有效。
- SPSC 两端缓存本端游标和已观察到的对端游标；只有缓存数据/空间不足时才重新读取共享游标，跨进程 publish 的 acquire/release 语义不变。
- 通知只发给已经登记的 waiter，同类 pending wake 会合并；data 与 space 同时 pending 时只需一次控制写。可复用 pulse 的锁存快路径在 10,000 次循环中本线程分配为 0。
- 256 KiB 帧通过 64 KiB 环时仍使用有界 staging，但 accumulated growth-copy 为 0；多段 spill 在中途取消并恢复 Flush 后仍保持字节顺序，pending growth-copy 为 0。

## 已覆盖证据

- Release solution build：0 warning、0 error。Unit 183/183、Generator 17/17、Integration 114/114；其中 SharedMemory 传输专项 27/27。
- 共享内存选项/profile、非法容量/SpinCount/timeout、路径权限、nonce/ack、未知控制信号、游标有符号溢出、越界 spill 和 stale 文件清理。
- 原始双向各 1,000,000 条带序号/checksum 记录，在 64 KiB 环上反复回卷，零损坏。
- 完整生成代理调用形态同时在 TCP 与 SharedMemory 上执行：Unary、Void、OneWay、client stream、server stream、duplex 及多流变体；另覆盖 1-byte stream/connection window 背压。
- 基础 RPC、256 KiB 超环帧、容量协商、连接池 1→2 扩容、多客户端认证上下文隔离与拒绝、心跳空闲、无服务 timeout、调用方取消、未知握手版本、nonce 错误、监听空闲、并发关闭、断连、重连和双方独立子进程强杀。
- PackageSmoke 从本地生成的 NuGet 包和全新 NuGet 缓存独立 restore/run，并分别完成 TCP 与 SharedMemory RPC；macOS arm64 SharedMemory NativeAOT server/client 独立进程通过，publish 无 trimming/AOT warning。Linux x64 与 Windows x64 framework-dependent publish 均为 0 warning/0 error，但没有冒充运行时验证。
- commit `6631eaf8f97eb147a6313393b1d3797a280ed281` 的 SharedMemory Chaos：120 秒、并发 32、23 次服务重启；2,127,782 success、757,618 expected failure、0 unexpected failure，最长恢复 220 ms。结束后五项 tracked metrics 与临时映射文件均为 0。

120 秒 Chaos 只有起止两个 retained-memory 样本，不能据此作泄漏判断，也不能代替 24 小时最后六小时增长门禁。

## 性能证据状态

当前没有有效的性能或 trace 结论。正式复测时，每个场景必须把 SharedMemory 与该平台所有适用本机传输同时交替比较：TCP、UDS、NamedPipe、AnonymousPipe；门槛中的“最快本机基线”是这些候选的最快者，不是只与 TCP 比较。某项传输不在平台支持矩阵内（例如本项目当前不承诺 Windows UDS）时须在报告中明确记录，而不是补造数据。

性能恢复条件是同机没有其他 LoadTest、StreamLoadTest、Chaos 或诊断采集进程。恢复后先记录 commit、OS、CPU、runtime、频率/电源设置和后台进程，再执行 5 秒预热、20 秒采样、正反顺序各五轮。任何 trace 只用于定位已稳定复现的瓶颈；未证明通知、映射或回卷是主因前，不开始平台特化。

## 正式门禁

1. Windows x64、Linux x64、macOS arm64 均执行 Release build、Unit、Generator、Integration、PackageSmoke，以及独立进程 SharedMemory NativeAOT smoke，要求零 warning/失败/trimming/AOT warning。
2. 环境隔离检查通过后，`SHARPLINK_MATRIX_TIER=full SHARPLINK_MATRIX_RUNTIMES=jit,aot eng/run-performance-matrix.sh` 保存全部原始 JSON。五轮采用交替顺序并取中位数。
3. 每个平台 64 KiB、Throughput、c32/c128 吞吐至少领先该平台适用的 TCP/UDS/NamedPipe/AnonymousPipe 中最快者 15%；0/32/256 B、LowLatency、c1/c8 的 P99 不超过最快者 105%；每请求分配不超过 105%；错误数必须为零。
4. TCP/UDS/NamedPipe/AnonymousPipe 相对 `6c3e277943b09413134425bca65c38447ca52a46` 满足 QPS ≥97%、P99 与 allocation ≤105%。
5. 三平台先执行 2 分钟 smoke，再执行 2 小时 nightly；最终候选运行 24 小时。要求零 unexpected failure/crash/deadlock、结束指标归零、最后六小时 retained memory 增长 ≤5%。

## 自动化入口

- 性能矩阵：`eng/run-performance-matrix.sh`
- JIT/NativeAOT 独立进程：`eng/run-shared-memory-aot-process-smoke.sh`
- 24 小时 Chaos：`SHARPLINK_SOAK_TRANSPORT=sharedmemory eng/run-release-soak.sh`
- PR、nightly、release 三套 GitHub workflow 已包含共享内存 smoke；nightly 的 AOT 与 2 小时 Chaos 使用三平台矩阵。

## 尚未解决/待证实

- 当前只实现统一命名管道通知后端。只有正式 trace 证明通知、映射或回卷路径是平台瓶颈时，才增加 eventfd/kqueue/Windows event 等平台特化，并以相同 A/B 拒绝无收益实现。
- 当前没有可接受的性能样本；分配门槛必须使用 `AllocatedBytes / Success` 比较，而不是只看进程总分配。
- Windows x64、Linux x64 的运行时正确性与 NativeAOT 均待各自宿主验证；现有 cross-RID framework-dependent publish 只证明编译，不证明运行兼容。
- 2 小时和 24 小时 Chaos 尚未对本候选执行；短时 retained-memory 增长不判定通过或失败。
- 其他架构只能做可行的 build smoke，不宣称完整支持。
