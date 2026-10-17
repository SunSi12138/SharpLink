# #775：只读请求封装构造的单点实验

假设：服务端在静态绑定成功后用显式构造替代 `request with { BoundMethod = method }`，
可能让实际 Tier1 派发去掉 96 字节封装的临时往返复制。尚未证明机器码改善或性能收益。

仅修改 `SharpLinkServer.StaticBinding.cs` 与 `ServerRequestEnvelopeReader.cs` 的实验编译路径。
原来的只读 record struct、字段布局、init-only BoundMethod 与五参数构造均保留；
增加的只读引用内部构造只在 Bind 的 token/MethodId/形态校验成功后使用。
没有 Unsafe 写只读内存、可变公共结构、generated ABI 变化、协议或容量/生命周期豁免。
失败路径及 UnsupportedShape 异常顺序不变。解码阶段的原有 with 表达式不在本次修改范围。

新增 smoke 对照验证显式构造与原 with 的值等价、metadata/方法对象身份、单调 deadline 完整字段、
分段参数序列位置和 owner 别名，以及 ReadDecoded 的普通/metadata 复用/分段回退。
原有 token、未知方法、形态、Session 局部性与预业务拒绝检查继续保留。

`issue-775-envelope.yml` 是只读、显式 `[issue775-envelope]` 触发的代码生成工作流。
旧绑定固定 `d2cb248b5e4b573d448092f7f152c2f2dd281e30`，候选固定触发提交完整 SHA；
相同 SDK、2CPU、tiering/PGO、真实 TCP/SharedMemory 的 Unary/OneWay，另进程采集 JIT。
不混入正式计时；Tier1 header 仅表明覆盖，不自动证明复制消除。
原始/交错 listing、命令/环境/二进制 hash 和失败全部保留。

先检查实际 DispatchRpcAsync / DispatchOneWayRpc Tier1，再决定是否值得正式复验。
机制不成立则保留负证据，不跑计时挑数。机制成立也须按原始 A 基线完成 ABBA/BAAB，
并与旧静态 B 在同机作配对对照；原 workload、分配预算和 +512 B/op 负对照不变。
不以代码字节数、独立 helper 的 Tier0 或诊断进程耗时替代性能验收。
PR 保持 Draft；不合并、不关闭 #775、不扩展 Replace/Receipt/Streaming。

## 首轮负证据与诊断修正

首候选 `d9b79269` 的代码生成运行 `39008328341` 保留为失败：Unary 的实际 Tier1
不再内联 ResolveStaticRequest，96 字节 bulk write barrier 仍在 callee 中。调用方变短
不是消除复制的证据，不因此运行性能测试。默认与实验功能检查已执行，另行报告结果。

第二种写法将逐字段参数读取移入只读引用构造，缩小 ResolveStaticRequest 的 IL，
继续保持同一字段重建假设；不强制内联、不禁用 tiering/PGO、不改可变性或协议。
测试直接对照该新构造与原 with，避免只验证未使用的构造入口。

首轮 OneWay 100,000 次诊断只观察到 Tier0，覆盖门禁正确失败；
后续仅将 JIT 诊断的 OneWay 执行量固定到 1,000,000，两个协议相同的静态臂均使用。
不改正式计时的 10,000 预热 / 100,000 测量，不重跑任何计时样本。
首轮 artifact `12091941131`，ZIP SHA256
`cc4e7ca35d1c9a54d2b470587f9ad846d96476e0ba6e1bed0f2cca45527a96d8`。
