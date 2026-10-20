# #775：静态解析的标量传递边界

本次仅修改静态实验路径的三个生产文件。ResolveStaticRequest 接收 BindingId/MethodId 标量，
输出精确 registration/method，不再通过 ref 修改整个 ServerRequestEnvelope。token/MethodId/
形态检查、异常顺序、容量/授权/取消/生命周期规则不变；不改字段布局、generated ABI、P2C、
基准 workload 或预算。Unary 调用方仍将方法放入自己的只读封装供后续管线使用。

复核发现已有无 metadata 压缩路径在 ReadRequestEnvelope 后会丢失 BoundMethod。
Unary 从解压前已验证的 boundMethod 重新附加，不重查当前注册、不重复解释 MethodId。
OneWay 现在始终直接使用同一个 boundMethod 局部引用，不再写入或从封装读取该对象；
descriptor、取消支持、调用上下文、实际调用都使用它，解压重读也不会丢失方法事实。
这包含正确性修复，不将整个补丁描述成纯等价构造优化。

## 第一候选负证据

`574543fde3fbbced42ddf25ab654e1a8bb19e1a1` 仅移除解析函数的 ref，再在两个调用方执行 with。
运行39088427221完成新旧四种真实传输/调用形态的诊断与Tier1覆盖，工作流success。
但实际Tier1的OneWay IG43/44仍有96字节临时往返复制，Unary同样没有消除目标复制。
工作流success只代表执行/覆盖，不代表机制成功。未启动正式计时。
artifact12121073854，ZIP SHA256：
`647643e3fc16141859533632a20574a160318ad70bd8d139498b6fbbcdf43927`。

第二候选只进一步改变OneWay的方法信息消费方式：直接消费局部boundMethod，避免将其放入
无后续消费者需要的整个封装。Unary不声称消除复制。须重新审阅实际Tier1再决定是否计时。

## 诊断与回归

代码生成旧输入固定a473f5d3a50d3b23874f8b1bb72f46e65ff939d1，候选固定触发提交。
工作流contents:read，所有.NET运行在Actions。默认/实验回归沿用已有workflow。
JIT诊断只叠加相同Program入口和BoundaryDiagnostics.cs，不叠加生产代码。
新旧同一VM/SDK/两个CPU/原tiering与PGO顺序执行。只过滤实际派发和解析函数；
每个完整Tier1 listing都须核对，覆盖成功、代码大小、诊断耗时均不等于收益。
Unary为8批×65,536次/c8p4；OneWay为512批×2,048次/c1p1。每批等待真实业务完成，
再以已有Unary响应提供诊断调度边界；不新增Receipt/ACK、不提高8MiB队列限额、不用固定sleep。
诊断次数、成功/失败、额外Unary及绑定数全部记录，不与原BDN或正式性能样本混用。

新增独立compression-regression workflow，在相同测试叠加下分别运行旧a473及候选，
TCP/SharedMemory×Unary/OneWay×有无metadata，使用真实Zstd压缩、真实业务和解压计数。
旧版应重现四个无metadata失败，候选八场景都须通过，且每场景确实解压一次才算有效覆盖。
不能用构建失败、未协商压缩、仅辅助解析器测试代替端到端回归。8KiB请求不覆盖persistent
大请求解压、所有admission/取消竞态或完整生产验收；结果以Actions实际输出为准。

机制确认前不启动新正式计时；确认后仍按固定源码对原始A和旧静态候选复验。
历史负证据不覆盖，不以不显著证明等价，不修改分配或约定门槛。
PR保持Draft，#775不关闭，不进入Replace、Receipt、Streaming。
