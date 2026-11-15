# N1-STATIC-BIND-20261115-V1：一次 Linux TCP 协议

状态：源码/协议待独立审核；未获 root 明确开跑授权。这个文件和 `test/SharpLink.N1.StaticBindEvidence` 必须作为同一个源码 subject 审核。actual base/head 写入交接记录；执行时 `N1_SOURCE_SHA` 必须等于 checkout 的完整 `git rev-parse HEAD`，不按分支名漂移。

## 执行边界

- 一个 GitHub-hosted Linux x64 job，SDK **10.0.115** / runtime **10.0.15**。不执行本机 RPC/ALC/分配/性能；无矩阵、重跑、压力/性能或 GC 实验。
- 整个 job hard **15分钟**，RPC 程序只执行一次：`dotnet test/SharpLink.N1.StaticBindEvidence/bin/Release/net10.0/SharpLink.N1.StaticBindEvidence.dll rpc N1-STATIC-BIND-20261115-V1`。入口脚本 `eng/run-775-n1-static-bind.sh` 要求 GitHub/Linux、精确 source 与显式协议 UID。
- 实际 loopback TCP；generated normal client 与 actual server。transport decorator 只观察/冻结真实 bytes/flush，不制造 Ack、token 或 completion，不反射改产品表。raw peer 也只通过真实 TCP 发协议帧。
- 接入原 send queue 默认8MiB/frame4MiB、默认流窗口与 capacity。单 endpoint pool1，配对 case pool2；client unary默认2秒，OneWay不继承它。固定 reconnect50毫秒/jitter1；只有 timeout case reconnect固定30秒，防止把新 Session 的成功误作旧 operation 成功。
- readiness budgets：普通 Prepare5秒；timeout case13秒。每 Bind原10秒。fixture事件/读写 guard3秒；client Stop guard3秒；server graceful Stop3秒、外层guard4秒。不因失败延长这些数值。timeout case在真实 Bind send flush 前冻结1秒，再让 actual server Ack 停在其 flush 前，保证先消耗原 client operation budget。

## 顺序、输入与 oracle

程序先执行纯组件项，随后严格按以下八个 case；首错即停止后续 case，仍释放所有 fixture gate、尝试每个已创建 client/server 的 cleanup，保留原错及 cleanup 错。

1. **generated-pool-session-pairing**：连接前同步 factory；未 bound 调用在 request Codec 前得到60004；实际 Unary `13→20`。让 `101` 调用持原 handler claim，再用普通 Picker 发 `17→24`，使两个真实 Session 都被调用。逐 Session 对照 actual Ack 与 Request token；数字 token 相同不等于错误。无输入 OneWay31在远端 handler仍被 gate 持有时本地已完成；caller取消99保留 OCE；foreign selector60001。
2. **early-ack-publication**：client output真实 flush 已转发 Bind bytes，但其返回被 gate 持有；观察 actual Ack 已收、尚未 publication return。Prepare不完成，调用60004且不序列化。放行后同 site Prepare成功，`4→11`。
3. **waiter-cancel-shared-bind**：actual server Ack flush前 gate；同 site两个 Prepare。取消第一个仅得到 OCE，第二个与唯一共享operation仍 live；放行后第二个成功，`2→9`。
4. **wire-token-facts-capacity**：wire4/5 handshake拒绝；wire6握手/Manifest S/epoch一致。错误hash Ack HashMismatch且无token。真实连续64次 creator Bind得到不同完整token，第65槽拒绝Capacity。未知完整token60106、未知method60107；release一个后复用同slot新incarnation；旧release不影响新entry，旧token60106；已知facts/OneWay形态冲突关闭Session。全组guard无 typed Codec/activation，连接退出后 table/ops/protected quota归零。
5. **queued-release-cut-physical-owner**：admission concurrency1，queue count1/64KiB/2秒。真实generated102持handler；raw peer自己真实Bind，使用观察到的同契约合法generated head grammar发第二个请求。等actual queue1且两个physical reader held；release raw binding先赢，主动取消queue，业务Codec/handler均不进入。queue退出只归还其原physical reader，已claim的第一调用仍按原budget完成109。
6. **disconnect-inflight-and-reconnect**：真实Ack flush前断开原 client transport；另一client/Session已独立完成Bind且保持可用。释放旧gate，原5秒Prepare预算内通过新Session真实Bind恢复，`10→17`，另一Session `5→12`。按新physicalSession匹配actualAck/token，不复用旧Session target。
7. **site-waiter-hard-bounds**：未连接client持64个不同完整receipt-key channel，第65site Capacity60104。64个有限Prepare占64waiter，第65waiter Capacity；取消这些waiter后逐个观察退出。Stop后site/waiter/binding/operation均0。无无限budget。
8. **operation-timeout-physical-close**：13秒Prepare，Bind仍是原10秒；前述1秒send gate与actual Ack gate使旧operation超时。预期DeadlineExceeded；该Session物理drain后operation quota归还。不会采用随后放行的旧Ack。

## 有效性、停止与离线消费

- **PASS** 必须：精确 source/UID/environment均成立，八个case全部结束，程序退出0、`N1_RESULT success` 与 `result.json outcome=PASS` 一致；cleanup完成。组件/源码不能代替实际RPC oracle。
- 非零退出保留第一条 `N1_FIRST_FAILURE` 全栈和全部cleanup错。fixture timeout/缺gate事件为 guard/INCONCLUSIVE；非法fixture帧/清理自身错误为fixture；业务/身份/owner/assertion反例为source；进程/job超时单列watchdog/INCONCLUSIVE。未完成不当作产品通过。
- 不自动重跑、不改oracle/预算、不增加GC、不关闭端点以掩盖live owner。真正的连接故障只关闭对应物理Session；source recovery/fixture修正先回原owner并重新独立审。
- job终态后先离线消费 raw stdout/stderr、result JSON、source/环境与 GitHub job JSON/HTML，核UID/输入/退出/guard有效性，再决定结果。保留非发布源码和恢复报告。临时ref/workflow只按自己创建时的精确lease清理；不触正式dev/其他owner ref，不提产品合并PR。
