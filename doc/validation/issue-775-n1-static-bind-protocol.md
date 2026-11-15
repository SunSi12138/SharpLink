# N1-STATIC-BIND-20261115-V2：一次 Linux TCP 协议

状态：V2诊断修订待独立审核；没有第二次运行授权，成本资格HOLD。V1（source `be24fda408118e5a9155174b8ac3c6b794e46aa9`）已消耗唯一一次授权：[run43693296449](https://github.com/SunSi12138/SharpLink/actions/runs/43693296449) 首case初始Prepare5秒失败，零RPCcase完成，并附client.Stop3秒guard。V1冻结协议、13文件artifact与首错trace保留在该source及本地`artifacts/n1-static-bind/hosted-once/`，不以V2覆盖其结果。这个文件和 `test/SharpLink.N1.StaticBindEvidence` 必须作为同一个源码 subject 审核。actual base/head 写入交接记录；执行时 `N1_SOURCE_SHA` 必须等于 checkout 的完整 `git rev-parse HEAD`，不按分支名漂移。

## 执行边界

- 一个 GitHub-hosted Linux x64 job，SDK **10.0.115** / runtime **10.0.15**。不执行本机 RPC/ALC/分配/性能；无矩阵、重跑、压力/性能或 GC 实验。
- 整个 job hard **15分钟**，RPC 程序只执行一次：`dotnet --fx-version 10.0.15 test/SharpLink.N1.StaticBindEvidence/bin/Release/net10.0/SharpLink.N1.StaticBindEvidence.dll rpc N1-STATIC-BIND-20261115-V2`。入口脚本 `eng/run-775-n1-static-bind.sh` 要求 GitHub/Linux、精确 source 与显式协议 UID，并用 `--fx-version 10.0.15` pin；RPC 入口再次核实 actual `Environment.Version == 10.0.15`，不是仅列出已装 runtime。
- 实际 loopback TCP；generated normal client 与 actual server。transport decorator 只观察/冻结真实 bytes/flush，不制造 Ack、token 或 completion，不反射改产品表。raw peer 也只通过真实 TCP 发协议帧。
- 接入原 send queue 默认8MiB/frame4MiB、默认流窗口与 capacity。单 endpoint pool1，配对 case pool2；client unary默认2秒，OneWay不继承它。固定 reconnect50毫秒/jitter1；只有 timeout case reconnect固定30秒，防止把新 Session 的成功误作旧 operation 成功。
- readiness budgets：普通 Prepare5秒；timeout case13秒。每 Bind原10秒。fixture事件/读写 guard3秒；client Stop guard3秒；server graceful Stop3秒、外层guard4秒。不因失败延长这些数值。timeout case在真实 Bind send flush 前冻结1秒，再让 actual server Ack 停在其 flush 前，保证先消耗原 client operation budget。

## 顺序、输入与 oracle

程序先执行纯组件与未连接 client 的无关 Codec publication/MC guard 控制（旧 local selector/channel/proxy仍有效、25毫秒有限 Prepare仍是Deadline而非60002；保留candidate clone隔离；MC enum/receipt/实际child metadata上限优先于终态，Stop后弱scalar guard仍生效），随后严格按以下八个 case；首错即停止后续 case，仍释放所有 fixture gate、尝试每个已创建 client/server 的 cleanup，保留原错及 cleanup 错。

1. **generated-pool-session-pairing**：连接前同步 factory；未 bound 调用在 request Codec 前得到60004；实际 Unary `13→20`。让 `101` 调用持原 handler claim，再用普通 Picker 发 `17→24`，使两个真实 Session 都被调用。逐 Session 对照 actual Ack 与 Request token；数字 token 相同不等于错误。无输入 OneWay31在远端 handler仍被 gate 持有时本地已完成；caller取消99保留 OCE；foreign selector60001。同 case 再接 actual A/B 两 TCP endpoints：二者 transport Ready，但 B 未暴露该 contract。偏好B的 custom selector实际只收到 qualified A candidate，Unary `6→13` 与无输入 OneWay32均走A的实际Ack/token/Session，不出现假60004。
2. **early-ack-publication**：fixed pool2 的一个 Session output 已转发 Bind bytes，但其 flush 返回被 gate 持有，actual Ack 已收而未 publication；另一 Session 已完整 Bind。同 site5秒 Prepare立即可用；持住已确认 Session 的101调用，再发 `4→11`，normal Picker仍只用已确认 Session，不把未 publication 的 peer当低负载候选。逐帧配对实际Session/Ack/token；放行后原active调用108，等同site published targets=2/operations=0，完整pair继续可用。
3. **waiter-cancel-shared-bind**：actual server Ack flush前 gate；同 site两个 Prepare。取消第一个仅得到 OCE，第二个与唯一共享operation仍 live；放行后第二个成功，`2→9`。
4. **wire-token-facts-capacity**：wire4/5 handshake拒绝；wire6握手/Manifest S/epoch一致。错误hash Ack HashMismatch且无token。真实连续64次 creator Bind得到不同完整token，第65槽拒绝Capacity。未知完整token60106、未知method60107；release一个后复用同slot新incarnation；旧release不影响新entry，旧token60106；已知facts/OneWay形态冲突关闭Session。全组guard无 typed Codec/activation，连接退出后 table/ops/protected quota归零。
5. **queued-release-cut-physical-owner**：admission concurrency1，queue count1/64KiB/2秒。真实generated102持handler；raw peer自己真实Bind，使用观察到的同契约合法generated head grammar发第二个请求。等actual queue1且两个physical reader held；release raw binding先赢，主动取消queue，业务Codec/handler均不进入。queue退出只归还其原physical reader，已claim的第一调用仍按原budget完成109。
6. **disconnect-inflight-and-reconnect**：真实Ack flush前断开原 client transport；另一client/Session已独立完成Bind且保持可用。释放旧gate，原5秒Prepare预算内通过新Session真实Bind恢复，`10→17`，另一Session `5→12`。按新physicalSession匹配actualAck/token，不复用旧Session target。
7. **site-waiter-hard-bounds**：未连接client持64个不同完整receipt-key channel，第65site Capacity60104。64个有限Prepare占64waiter，第65waiter Capacity；取消这些waiter后逐个观察退出。Stop后site/waiter/binding/operation均0。无无限budget。
8. **operation-timeout-physical-close**：13秒Prepare，Bind仍是原10秒；前述1秒send gate与actual Ack gate使旧operation超时。预期DeadlineExceeded；该Session物理drain后operation quota归还。不会采用随后放行的旧Ack。

## V2诊断及相邻Stop修复，不修改八case与预算

- 源码审计及确定反例见[Prepare失败审计](issue-775-n1-prepare-failure-audit.md)。本轮另外修复cluster Stop没有清site/关闭Bind工作准入，以及显式Stop早于Seal时refresh的预期ConnectionClosed被误记framework故障；6项纯控制证明原site归还和窄异常过滤。它们不解释V1的fixed初始Prepare，不声称首错已修。
- 失败采样的每个gate段用零等待TryEnter；site index/history/site/binding/topology/supervisor以及fixture frame/logger分别报告available/contended/failed。拿不到的site/target计数为null，不能把未知写成0；即使单个gate争用，仍保留其Session/contract scalar身份。Until predicate/guard observation也使用这些非阻塞观测或原atomic计数；未知只是不满足predicate，仍按原3秒guard报告INCONCLUSIVE。没有Task.Run采样或超时后仍持owner的任务；回调失败释放已取得的gate。原deadline只读scalar诊断投影，不调用TimeProvider或transport安全getter。
- Close先完成原Ack waiter信号、释放原binding gate，才作best-effort的非阻塞采样和history记录；诊断throw/争用只记failed/drop计数，不拦原物理cleanup。新增10项纯争用控制验证实际site-index/supervisor gate未释放时返回、单段失败以及后续cleanup，未建立假Session/Ack。
- 每case在第一错误后、cleanup前输出一次 `N1_FAILURE_SNAPSHOT`；cleanup失败后再采一次。每份只是恢复时的逐项采样，不是跨组件原子快照或原signal时间证明；诊断捕获自身失败单列`N1_DIAGNOSTIC_CAPTURE_FAILURE`，仍保留原错并尝试cleanup。
- client内部只读trace含真实readiness与Session protocol phase/connected/admission、队列字节/idle wait、实际Bind op predecessor/send/Ack/result Task状态、原deadline诊断投影、confirmation与同site targets。可区分未issue、前序未发布、queue/flush、已收Ack未publication、confirmed未target；错误仅保留类型/code/detail。开启诊断的fixture额外保留前3与最新1个Session closure采样、每Session首个失败op；这些是closure时的历史值，不是当前仍live的owner。
- Stop采样显示实际await阶段（取消、connection Fail/Dispose、registry/framework drain或资源清理）及相关Session。并读原FrameworkTaskSupervisor的有界active/failure计数；fixture logger保留前32/最新32条已有warning/error的event/type/code/detail/最多4096字符stack，最多4层cause，绝不格式化state或exception消息。
- transport decorator仍只操作真实Socket/Pipe。原512帧界内只输出header/correlation/长度/control token，不输出payload/auth/metadata；outbound记录在transport flush前，仅代表staged bytes，inbound记录在consume边界。新增每Session最后一次实际inner flush Task状态/阶段（最多512Session），不生成Ack/业务completion，不用诊断信号代替正常Picker/Session或延长guard。默认未指定gate的case使用同种passive recorder，以留下首个Bind的双向轨迹。
- 本机仅新增9项纯诊断组件（脱敏、64条界与首/尾保留、header-only、managed Pipe实际flush Task透传、未连接site/实际Stop阶段），无本机RPC。原23+11+4及负编译不因添加诊断重复；hosted仍只先执行原23+11与原八case，首错停止。V2不是V1授权的续跑或重试。

## 有效性、停止与离线消费

- **PASS** 必须：精确 source/UID/environment均成立，八个case全部结束，程序退出0、`N1_RESULT success` 与 `result.json outcome=PASS` 一致；cleanup完成。组件/源码不能代替实际RPC oracle。
- 非零退出保留第一条 `N1_FIRST_FAILURE` 全栈和全部cleanup错。fixture timeout/缺gate事件（含RawPeer自己的3秒CTS抛OCE）为 guard/INCONCLUSIVE；guard边界记录 `N1_GUARD` 的 actual source Task与WaitAsync promise状态及恢复时elapsed，predicate等待另记实际观测。恢复时已完成不反推原signal发生时刻，不把guard失败转为source成功；非法fixture帧/清理自身错误为fixture；业务/身份/owner/assertion反例为source；进程/job超时单列watchdog/INCONCLUSIVE。未完成不当作产品通过。
- 不自动重跑、不改oracle/预算、不增加GC、不关闭端点以掩盖live owner。真正的连接故障只关闭对应物理Session；source recovery/fixture修正先回原owner并重新独立审。
- job终态后先离线消费 raw stdout/stderr、result JSON、source/环境与 GitHub job JSON/HTML，核UID/输入/退出/guard有效性，再决定结果。保留非发布源码和恢复报告。临时ref/workflow只按自己创建时的精确lease清理；不触正式dev/其他owner ref，不提产品合并PR。
