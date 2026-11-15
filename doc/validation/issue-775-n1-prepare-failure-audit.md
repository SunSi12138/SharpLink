# N1 首次 Prepare 失败：已知边界与下一判别

## V1 确认失败，没有完成 RPC case

唯一运行是 [43693296449 / job132748271127](https://github.com/SunSi12138/SharpLink/actions/runs/43693296449)，attempt1；审定 source `be24fda408118e5a9155174b8ac3c6b794e46aa9`，tool commit `021f6b84a55c95db5eb525af51bd54a5bc207536` 只加 workflow，parent 为 source。SDK10.0.115、实际 runtime10.0.15、UID V1 与退出1均已核对。

原23项组件、11项 publication/MC 控制与连接前两项 guard 共36项通过；首case在第一次 `proxy.PrepareAsync(5秒)` 得到 DeadlineExceeded，尚未取得 Unary 返回，后7case未执行。cleanup另遇 client.Stop 的3秒 guard：恢复时原 Task 为 WaitingForActivation，独立 WaitAsync promise 已 fault；server最终 binding/operation/protected 三项为0。client清理没有被证明完成。

13文件 artifact、完整 job log、run/jobs/artifacts JSON、可用 run/job HTML、环境与冻结协议均在本地 `artifacts/n1-static-bind/hosted-once/`。该 artifact 没有首错时的各 Session frame/op/flush/Ack/target 清单；不能从缺少日志推出“没发 Bind”“没收 Ack”或“transport Ready 一直保持”。V1授权已消耗，V2没有运行授权。

## 因果审计没有唯一定位首个 Prepare 的内部原因

以下核对以 V1 实际 source 为对象，后续诊断不改这些 RPC 规则。

1. **factory 与原 site**：generated ProxyFactory 得到 ContractChannel；Service 与 Prepare 共用其 canonical site。构造提交后触发 refresh；无 route 调用在序列化前拒绝，V1两项前置控制确已通过。不存在旧 ContractId facade 或另一个 Prepare site。
2. **Connect 与握手**：`ConnectOneAsync` 先完成真实 handshake，验证成功 response、`TryCompleteHandshake` 和同 Session Manifest，随后创建 ClientConnection、发布 ready snapshot、启动 reader/heartbeat。`ConnectAsync` 返回说明其初始化任务完成，不保证此前 Session 到 Prepare 时仍 Ready；后续断线、协议失败或重连必须另看当时的 Session 状态。
3. **资格与 refresh**：Manifest/readiness/factory/Prepare 调度同一个 one-running/one-dirty refresh。它捕获实际 ready connections，按同 Session Manifest/hash 冷资格决定 Bind；每 Session 返回共享 Bind Task。Prepare在原5秒内等待 site 的 `_changed`，不取消原10秒 Bind。没有 eligible target 时得到 deadline，与“Bind 从未启动”并不等价。
4. **原 queue/flush**：`ClientBindingSession` 先预留绑定/op并关联 Publishing，然后等待 predecessor，调用 `ContractBindingWire.SendAndFlushWithinBudgetAsync`；该函数仍走原 normal queue、真实 pump 与 flush completion。早到 Ack 不能越过 publication；10秒包含所有等待。不确定失败先发布原 cause，再取消并 join 原 Session/pump/send，不能提前归还 quota。
5. **server 与 Ack**：真实 reader解析 Bind；同静态R的临时 metadata reader保护冷 resolver/full facts，构造 immutable table、安装完整 token，真实 Ack 再走 queue/flush。错误hash/facts等能结构化拒绝，但 V1 首错没有足够相位数据判断是否走到这里。
6. **confirmation 与 target**：client reader按真实 Session分派 Ack；original deadline、完整 token/watermark与receipt通过后完成 BoundTarget Task。refresh按完成顺序批量发布 qualified snapshot。Prepare读取同 site 的实际confirmed target；成功条件检查、signal捕获与publication都在原site gate协调，未见可从现有trace唯一推出的lost-wakeup反例。
7. **Stop关联**：fixed Stop关闭本地准入/site，取消lifetime，Fail每个connection使 Bind Ack waiter退出，再await Session物理Dispose、registry和framework drain。原Task可能停在connection/pump/transport cleanup，也可能在framework任务收尾；V1单个Stop guard状态不能区分，更不能据此改变3秒边界或断言owner泄漏。未修改上述物理join顺序。

因此没有按猜测修复首个 Prepare，亦未修改RPC期限、优先序、quota或重新运行。下一次若获授权，应先用失败时的实际状态区分：未进入refresh/没有qualified Session、predecessor阻塞、send queue/inner flush阻塞、已收Ack尚未publication、confirmation失败、confirmed却未同site发布。

## 已证明并修复两个相邻 Stop 问题

这两项不解释首跑的 fixed-pool 初始 Prepare；它们属于同一 N1 的控制生命周期和限额归还。

- **cluster未清site/关闭Bind工作准入**：旧 `StopStaticClusterCoreAsync` 没有fixed Stop的 `_poolStopping` / `StopContractSitesLocked` 步骤。纯组件创建实际两endpoint client但不Connect，持有真实proxy，先作25毫秒empty Prepare以排除未退出refresh，再Stop；原subject确定在site归还断言失败。修后沿原 `_poolGate` 关闭工作准入、清site并seal；不持该gate运行cluster callbacks或Dispose。旧key不能依赖GC才归还。
- **显式Stop早于supervisor Seal的预期refresh退出**：第一版纯控制另实见 Refresh 的 ConnectionClosed 先被supervisor记录，使Stop重抛。`_stopStarted` 在Stop入口已置位，而BeginShutdown/Seal随后才发生。现在仅在显式Stop已开始且异常为ConnectionClosed时，把该控制任务视为正常退出；仍唤醒原waiters并记录error。非Stop的ConnectionClosed、DeadlineExceeded和非预期错误仍fault，不吞业务结果或物理cleanup错误。

失败前输出保留在 `cluster-stop-before-fix.txt`、`cluster-stop-settled-before-fix.txt`。修后6项纯控制通过，包含上述实际site、finite Prepare、归还及窄filter反例；没有网络/RPC/ALC。原23+11+4与负编译沿未变subject复用。

## V2只为下一次同预算判别补足信息

失败前与cleanup失败后各采一次：真实readiness/Session phase、queue字节/idle、site refresh资格数与阶段、原Bind的predecessor/send/Ack/result Task状态、deadline诊断投影、confirmation/token/targets、Stop实际await段及原framework任务状态。fixture保留有限的已有error类型/code/detail/stack，不格式化消息/state；frame只输出header/长度/control token。outbound观测是flush前staged bytes，不能冒充已上网；另采真实inner flush Task的状态。

closure历史限前3与最新1个Session，logger限前32与最新32条，每条最多4层cause及4096字符stack，帧和flush Session inventory各最多512。诊断不持Codec/Type/业务payload/凭据或异常对象根，不在普通业务调用上新增快照/日志。各记录是其采样时刻的事实，不能拼成跨组件原子快照，也不证明旧signal先后。

独立续审指出原同步诊断可能等source gate阻止cleanup，已改为逐段零等待TryEnter，未知值显式contended/null；Close先signal原Ack再诊断，history记录争用/失败不阻止cleanup。没有遗留采样Task。新增10项纯争用/失败控制通过（实际site-index和原supervisor gate持有期间返回，随后清理正常）；这不是首Prepare根因。

新增9项纯诊断控制通过：脱敏、界限/首尾保留、header-only、managed Pipe真实pending/完成flush透传、未连接site与Stop阶段。诊断构建通过；没有第二次hosted或本机RPC。后续仍须独立审核实际source及[协议V2](issue-775-n1-static-bind-protocol.md)，由root另决是否运行。功能资格未通过，成本资格仍HOLD。
