# #775：静态解析的标量传递边界

本次仅修改静态实验路径的三个生产文件。ResolveStaticRequest 接收 BindingId/MethodId 标量，
输出精确 registration/method，不再通过 ref 修改整个 ServerRequestEnvelope。两个派发调用方
只在原失败分支返回之后将方法放入自己的只读封装。token/MethodId/形态检查、异常顺序、
容量/授权/取消/生命周期规则不变；不改字段布局、generated ABI、P2C、基准 workload 或预算。

复核发现已有无 metadata 压缩路径在 ReadRequestEnvelope 后会丢失 BoundMethod。
当前从解压前已验证的 boundMethod 重新附加，不重查当前注册、不重复解释 MethodId。
这一点是正确性修复而非等价变换；需另行真实压缩 RPC 回归，不以旧443项检查冒充覆盖。

代码生成旧输入固定 a473f5d3a50d3b23874f8b1bb72f46e65ff939d1，候选固定触发提交。
工作流 contents:read，所有 .NET 运行在 Actions。默认/实验回归沿用已有 workflow。
JIT诊断只叠加相同的 Program 入口与 BoundaryDiagnostics.cs，不叠加生产代码。
新旧在同一VM/SDK/两个CPU/原tiering与PGO下顺序执行。只过滤实际派发和解析函数，
每个完整 Tier1 listing 都应人工核对；覆盖成功、代码大小、诊断耗时均不等于收益。

Unary为8批×65,536次/c8p4，OneWay为512批×2,048次/c1p1。每批等待真实业务完成，
再以已有Unary调用提供诊断调度边界；不新增线上Receipt/ACK，不提高8MiB队列限额，
不使用固定sleep决定完成。诊断次数、成功/失败、额外Unary数和方法绑定数全部记录。
这不是原BDN工作负载或正式性能样本，后者仍用未插桩干净构建。

机制确认前不启动新的正式计时；确认后仍须按固定源码对原始A和旧静态候选复验。
历史负证据不覆盖，不以不显著证明等价，不修改分配或约定门槛。
PR保持Draft，#775不关闭，不进入Replace、Receipt、Streaming。
