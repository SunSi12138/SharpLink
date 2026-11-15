# #775 N1 静态 Bind：非发布源码检查点

本分支从 dev `0ff57325761683fbc30a8d3561ddb43f578d0e9f` 建立。冻结前已正常纳入 dev `2948de82fff77d66fce51ecec9a5783b6ef3cb62`：保留 #991 test-only 更新及 #985 已采用的 cold publication 隔离。合同为 [N0](issue-775-n0-contract.md)。这是静态 Unary / 无输入 OneWay 的风险检查点，不能作为产品 PR 合入 dev。

## 当前真实入口

```csharp
var proxy = client.CreateProxy<IMyContract>(); // 同步、本地；连接前可调用
await client.ConnectAsync();
await proxy.PrepareAsync(TimeSpan.FromSeconds(5), cancellationToken);
var result = await proxy.Service.MyUnaryAsync(request, cancellationToken);
```

手写调用先取得 `client.GetLocalContract<T>()`，再用 `CreateContractChannel(local, options)`。generated Service 与 Prepare 使用同一个 canonical site。metadata 只在 wrapper 固定，非 null per-call metadata 覆盖它；不进入 Bind 身份。

`Get`、`GetWithMetadata`、`IRpcChannel` 与 MethodId-only Stub SPI 已移除；没有 legacy/raw forwarding 管线。未迁移的输入 OneWay、ClientStreaming、ServerStreaming、Duplex 与动态本地 contract，在这个非发布切片明确拒绝。

## 身份、准入与唯一 owner

1. factory 校验结构/终态、issuer/精确 local generation、hash 与完整 manifest facts，再原子提交最多64个弱 canonical site。失败的 proxy factory 撤回未提交 construction reservation。local selector 只含 scalar；site 可以持本地 generated/Codec owner。cold candidate wrapper 仍按 #985 克隆，但精确传递独立 logical registration identity；只有真实替换/退休/Stop 关闭其 cheap lifetime gate，无关 publication 不换 generation。普通 RPC 只检查捕获的 gate/full facts，不扫 registry 或 Manifest。
2. readiness/Manifest 事件合并到每 site 一个 running refresh 与 dirty bit。普通目标要求同数据 Session 的 Manifest 精确 hash；receipt 路径先核成功握手 S/epoch。一个有限 Prepare waiter 的取消不取消共享 Bind。按 Bind completion order 增量发布每批已确认的真实 targets；一个慢 Session 不隐藏另一个已确认 Session。
3. 每物理 Session 在暴露 bytes 前预留 binding/operation quota 和 Publishing 关联。operation ID 单调；显式 predecessor 排序发布。实际 normal send queue/flush 返回前，真实 early Ack 只能暂存。original10秒覆盖排队、flush、Ack；不确定失败关闭该 Session，join 其真实 pump/transport 后归还 operation quota。
4. 成功双方 wire6/floor6；API6 的 exact identity 是 `sharplink-3.0-api6-contract-channel-v1`。成功握手55B起、Manifest44B header，冻结真实非空 S/epoch；不同物理 Session 即使数字 token 相同，也各自拥有 entry。
5. server Bind 先取得原静态 R 的临时 metadata reader（service/Codec disposal 等它真实退出；不占 business call capacity），在该保护下冷解析 required resolver，完整 facts 与 manifest 全值相等，构造最多256方法的 immutable table；不激活业务、不占 call lease。Session64槽用完整 incarnation+slot token；release 后等真正 reader 退出才 detach/归还，并递增 incarnation 后复用。
6. 请求先 full token→same-entry physical protection→完整 facts/shape，再进入现有 admission、queue、decode、interceptor、activation/Stub。错误 hash、unknown token/method 与形态冲突不会进入 typed business Codec/activation。
7. `ServerBindingRequest` **继承并复用原 ServerRequestPermit**，不是额外套一个 release owner：同一个对象先持 R/facts 的 physical protection，queue 之后才接收原 local/global call capacity；decode、CTS、scope/输出 join 与最终 release 仍在原 permit 链。envelope/selection/map 都借用它，不重查 latest、不在退休 gate reacquire。
8. entry release 与 business-start claim 用 entry gate 仲裁；server Stop 用原 lifecycle gate 仲裁。cut 先赢的 Waiting 请求选定 reject，再在锁外用原 pooled call-state 的 generation-safe lease 发 queue-only 通知；claim 先赢者保持原 active/grace 语义。quota 只在实际 cleanup 归还。

同一个 site publication 冻结 qualified targets、connection arrays、endpoint candidates 与 retry exclusion 的索引空间。固定池与 endpoint-cluster 都交给原 Picker/同一个提取后的 built-in kernel，随后 reserve 同一 snapshot 的真实 Session；custom selector 只看 eligible candidates，不先选全 Ready 再报60004。endpoint policy 每 attempt 捕获一次；候选 membership 不变时保留 snapshot identity 和 retry index 空间。

## 已有证据与下一门

本机只运行 basic build、纯非 RPC components 和精确负编译 oracle。SDK10.0.115/runtime10.0.15；Client/Server/Generator/Hosting/GenerationControl 及实际 generated evidence consumer 编译通过。原23项组件证据复用；V1修订新增的11项真实无关 publication/MC guard与4项共享Picker kernel纯组件通过。MC factory一次捕获实际child route及其metadata上限；Stop清route后仅保留弱Type-key的scalar上限，metadata结构错误仍先于终态且不保留业务owner。组件检查确认32B无引用 facts、full ticks/null/负值、wire payload、65536 binding /1024 operation ledger 硬界与归还、API6/full resolver 与 generated facts。旧 Stub 负编译精确命中 required resolver 和四个新签名的五条 CS0535。

V1源码/协议已独立批准并执行唯一一次 [Linux TCP run43693296449](https://github.com/SunSi12138/SharpLink/actions/runs/43693296449)：首case初始同site Prepare5秒DeadlineExceeded；只有连接前fail-fast，零RPCcase完成，后7case未执行。client.Stop另遇3秒guard，实际source Task仍未完成；server最终三项ledger为0不能证明client清理。N1功能资格未通过。完整原始证据保留在`artifacts/n1-static-bind/hosted-once/`；运行绑定source be24fda4，所有临时ref/workflow已精确清理，没有重跑。

本轮沿factory→handshake/readiness→refresh/Bind→queue/flush→Ack/confirmation→target及Stop源码审计，未取得能唯一解释首错的确定性反例；不以猜测修产品。V2增加失败相位诊断与9项纯组件；续审修正为逐段零等待TryEnter、显式contended/failed/null，另10项纯争用/失败控制证明gate未释放时返回且cleanup继续。Close先发布原Ack信号再作best-effort诊断，不等待或依赖采样；另以6项纯控制修复cluster Stop遗漏site归还/关闭Bind工作准入，以及显式Stop早于Seal时refresh预期ConnectionClosed的误记；两者不解释V1首错，见[因果审计](validation/issue-775-n1-prepare-failure-audit.md)。现有trace没有首错当刻的Bind/flush/Ack/target数据，无法把deadline更窄归因。新[协议V2](validation/issue-775-n1-static-bind-protocol.md)保留原八case/全部预算，下一门仍是独立源码/协议审核，任何第二次hosted必须root重新决策；V1授权已消耗。

## 不能从本切片推出的结论

- 组/receipt publication、动态 registration/ABA、五族 input/output/stream aggregate、全部 caller/测试/样例/文档/包/Trim/AOT/#805/canonical 都未完成。旧 solution/Unit/Integration 等尚未整体迁移，不能冒称本分支全套绿色。
- N1 的 static R publication generation 为0；动态 R 明确拒绝。没有实现动态 Register/Replace receipt 生产，不伪造旧操作结果。
- 当前 owner representation 的**成本资格 HOLD**。下表是 x64 源字段估计，不是分配/性能实测；同为一个对象不等于同样 B/op。先完成 N1 正确性与本协议资格，下一段先处理 owner 表示/可证成本路径，再另决成本验证或功能扩展；不把这个风险拖到五族整包末尾，不改变16B预算，也不复制 D 压缩布局。

  | 对象/新增字段 | 源字段风险 |
  |---|---|
  | P `ServerRequestPermit` | 默认成功 Unary/无输入 OneWay 一个对象，x64 估计约64B。 |
  | N `ServerBindingRequest` | 同一原 permit 扩展的一个对象；`_session/_quota/_entry/Service` 四引用（32B）、`RequestId/Token`（16B）、`_waitingLease`（24B）、`Method`完整facts（32B）、`_claimed/_rejected`（2B）与`_transferred`（4B）；base新增`_capacityOwned`（4B）与原state共同位于原padding范围的估计；x64字段下界约176B，相对P约+112B/成功请求，尚未实测。 |
  | 其他工作 | reader/hash-map操作及 cold sites/targets/candidate/control 状态另计；没有当前40B删除或无耗时结论。 |
- 不恢复 #387 Windows RetryStop 或 SHM 调查，不拷入未采纳的上游候选代码，不发布产品包/PR，不关闭 #775/#805。
