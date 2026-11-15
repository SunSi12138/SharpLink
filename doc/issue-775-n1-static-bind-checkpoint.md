# #775 N1 静态 Bind：非发布源码检查点

本分支从 dev `0ff57325761683fbc30a8d3561ddb43f578d0e9f` 建立。合同为 [N0](issue-775-n0-contract.md)。这是静态 Unary / 无输入 OneWay 的风险检查点，不能作为产品 PR 合入 dev。

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

1. factory 校验结构/终态、issuer/精确 local generation、hash 与完整 manifest facts，再原子提交最多64个弱 canonical site。失败的 proxy factory 撤回未提交 construction reservation。local selector 只含 scalar；site 可以持本地 generated/Codec owner。
2. readiness/Manifest 事件合并到每 site 一个 running refresh 与 dirty bit。普通目标要求同数据 Session 的 Manifest 精确 hash；receipt 路径先核成功握手 S/epoch。一个有限 Prepare waiter 的取消不取消共享 Bind。
3. 每物理 Session 在暴露 bytes 前预留 binding/operation quota 和 Publishing 关联。operation ID 单调；显式 predecessor 排序发布。实际 normal send queue/flush 返回前，真实 early Ack 只能暂存。original10秒覆盖排队、flush、Ack；不确定失败关闭该 Session，join 其真实 pump/transport 后归还 operation quota。
4. 成功双方 wire6/floor6；API6 的 exact identity 是 `sharplink-3.0-api6-contract-channel-v1`。成功握手55B起、Manifest44B header，冻结真实非空 S/epoch；不同物理 Session 即使数字 token 相同，也各自拥有 entry。
5. server Bind 先取得原静态 R 的临时 metadata reader（service/Codec disposal 等它真实退出；不占 business call capacity），在该保护下冷解析 required resolver，完整 facts 与 manifest 全值相等，构造最多256方法的 immutable table；不激活业务、不占 call lease。Session64槽用完整 incarnation+slot token；release 后等真正 reader 退出才 detach/归还，并递增 incarnation 后复用。
6. 请求先 full token→same-entry physical protection→完整 facts/shape，再进入现有 admission、queue、decode、interceptor、activation/Stub。错误 hash、unknown token/method 与形态冲突不会进入 typed business Codec/activation。
7. `ServerBindingRequest` **继承并复用原 ServerRequestPermit**，不是额外套一个 release owner：同一个对象先持 R/facts 的 physical protection，queue 之后才接收原 local/global call capacity；decode、CTS、scope/输出 join 与最终 release 仍在原 permit 链。envelope/selection/map 都借用它，不重查 latest、不在退休 gate reacquire。
8. entry release 与 business-start claim 用 entry gate 仲裁；server Stop 用原 lifecycle gate 仲裁。cut 先赢的 Waiting 请求选定 reject，再在锁外用原 pooled call-state 的 generation-safe lease 发 queue-only 通知；claim 先赢者保持原 active/grace 语义。quota 只在实际 cleanup 归还。

固定 endpoint 的 site snapshot 同时冻结 targets 与 connection array，直接使用既有 `EndpointSelectionKernel.SelectConnection` 与真实 call reservation；一个 attempt 只捕获一个不可拆的 target。endpoint-cluster 路径保留原 selector/reservation，再按实际返回 Session 验 token；其完整 contract-qualified topology snapshot/多 endpoint 资格仍是后续迁移项。

## 已有证据与下一门

本机只运行 basic build、纯非 RPC components 和精确负编译 oracle。SDK10.0.115/runtime10.0.15；Client/Server/Generator/Hosting/GenerationControl 及实际 generated evidence consumer 编译通过。组件检查确认32B无引用 facts、full ticks/null/负值、wire payload、65536 binding /1024 operation ledger 硬界与归还、API6/full resolver 与 generated facts。旧 Stub 负编译精确命中 required resolver 和四个新签名的五条 CS0535。

实际 TCP、队列 cut、early/late Ack、取消/断线、物理 quota 归还只写入了[有界 hosted 协议](validation/issue-775-n1-static-bind-protocol.md)，**尚未运行**。下一门是未参与设计者的独立源码/协议审核；通过后还须 root 明确授权一次 hosted run。

## 不能从本切片推出的结论

- 组/receipt publication、动态 registration/ABA、五族 input/output/stream aggregate、完整 endpoint-cluster qualified routing、全部 caller/测试/样例/文档/包/Trim/AOT/#805/canonical 都未完成。旧 solution/Unit/Integration 等尚未整体迁移，不能冒称本分支全套绿色。
- N1 的 static R publication generation 为0；动态 R 明确拒绝。没有实现动态 Register/Replace receipt 生产，不伪造旧操作结果。
- 新 owner 是原 permit 的单对象扩展，但增加了 scalar/reference 字段、token-table/map/reader bookkeeping 和 cold Bind/target/control 状态。这里没有计算实际 managed object 大小、分配或耗时；没有删除所谓当前40B、没有采用历史 D 布局。最终成本/16B预算与无新增稳定每-call allocation 的资格仍须在完整采用 HEAD 单独证明。
- 不恢复 #387 Windows RetryStop 或 SHM 调查，不拷入未合并 #985/EOF 代码，不发布产品包/PR，不关闭 #775/#805。
