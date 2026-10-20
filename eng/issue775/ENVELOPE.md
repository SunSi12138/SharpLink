# #775：只读请求封装构造实验的负结果

## 阶段决定（2026-10-17）

两种显式构造都未达到“消除实际稳态派发的请求封装临时复制”机制门槛。
已恢复 `src/` 到实验前 `111ebd728cb9106f973bd83673a2418c93be5202` 的内容，
不保留未验证优化，不运行新的正式计时来碰结果。历史提交、全部失败与原始反汇编保留。
本结论仅否定这两种构造改写，不否定绑定设计，也不声称找到了 OneWay 回退的完整根因。
PR 保持 Draft；不合并、不关闭 #775、不扩展 Replace/Receipt/Streaming。

## 假设与边界

假设：将成功校验后的 `request with { BoundMethod = method }` 改为显式只读构造，
可能消除 96 字节封装的临时往返复制。两候选均保留 readonly record struct、init-only 字段、
完整 token/MethodId/形态校验及其失败顺序，没有 Unsafe、公开可变结构、generated ABI、
P2C/admission、压缩策略、队列限额、原始 benchmark 或分配预算变更。

旧静态源码固定 `d2cb248b5e4b573d448092f7f152c2f2dd281e30`，新旧均开启绑定。
每轮在同一 VM、同 SDK、两个可用 CPU、ProcessorCount=2、原 tiering/PGO 下采集。
这是独立 JIT 诊断，不是吞吐/CPU/等价验收；不可把不同 VM 的绝对时间混池。

## 第一候选：逐字段六参数构造

提交：`d9b79269a9a8d6a8b7726645617c72d21b8eaf12`。
代码生成运行：https://github.com/SunSi12138/SharpLink/actions/runs/39008328341
回归运行：https://github.com/SunSi12138/SharpLink/actions/runs/39008328292

实际 TCP/SharedMemory Unary Tier1 不再内联 ResolveStaticRequest，callee 中仍有
96 字节 CORINFO_HELP_BULK_WRITEBARRIER。调用方变短不等于消除了工作。
OneWay 100,000 次诊断仅采到 Tier0/Instrumented Tier0，四个新旧传输组合都未满足覆盖门槛。
代码生成 workflow 保持 failure；默认/实验构建、安全检查、单元/生成器、格式和维护性步骤通过。

artifact `12091941131`，ZIP SHA256：
`cc4e7ca35d1c9a54d2b470587f9ad846d96476e0ba6e1bed0f2cca45527a96d8`。

## 第二候选：只读引用构造

提交：`429697f525be40e167cf4a9ec6aa8165bcb93576`。
代码生成运行：https://github.com/SunSi12138/SharpLink/actions/runs/39008772251
回归运行：https://github.com/SunSi12138/SharpLink/actions/runs/39008772209

将逐字段读取移入 `new ServerRequestEnvelope(in request, method)`，没有强制内联。
SharedMemory Unary 实际 Tier1 恢复内联，但仍先搬运 Arguments/Deadline，
再通过 64+32 字节复制写回封装并补写字段；不是原先假设的单字段写入。
旧调用方 IG37/38 的整结构往返改成更多字段暂存及 IG42 的整结构写回，不能称为复制消除。
独立 helper 的 bulk barrier 仍存在，但已内联的调用路径不能据此声称每次执行该 helper。
该轮两个 TCP Unary 未采到实际 Tier1，不用其他版本替代缺失证据。

为补 OneWay Tier1，第二轮只将诊断调用量提高到 1,000,000；
结果新旧 TCP/SharedMemory 四个组合均触发原有 8 MiB send_queue_capacity，全部保留为失败。
这不是新候选特有的性能回退，未提高队列限额，未把未完成工作当作性能样本。
当前两轮资料不足以比较 OneWay 的优化后代码；不声称 OneWay 机制已通过。
后续诊断应使用预先固定的有界批次和明确完成边界，而不是无界加长发送循环。

artifact `12091897858`，ZIP SHA256：
`efb3ca120a5f24bf454b0d0c9dddeab6d89a72a5a95bcb1500503148cb8c1f2a`。
代码生成 workflow 保持 failure；默认/实验构建、安全检查、单元/生成器、格式和维护性步骤通过。

## 测试保留与复现

两候选各完成 2,363 项 smoke 断言：原有 283 项 + 每种传输 1,040 项封装检查。
其中构造专用的差分测试已随被撤回构造保留在历史提交及 source.tar.gz 中，不再留无效调用。
当前保留 ReadDecoded 的 80 项/传输（160 项）回归：路由字段、精确方法身份、完整 deadline、
metadata、分段回退与 decoded owner 别名。它们直接测试辅助解析器，
**不是端到端压缩 RPC、所有取消竞态或生产协议完整性证明**。
原有 283 项检查保留；当前预期 smoke 共 443 项，实际执行状态以当前提交的 Actions 为准。

`issue-775-envelope.yml` 改为只读的手动归档复现入口，只能选择上述两份历史候选，
并执行对应候选内的原始脚本和诊断量。不会对已回退的当前 HEAD 自动启动无意义采样。
失败不被 continue-on-error 隐藏。原始/交错 listing、命令、环境、DLL hash 和 source 都保留。

后续可检验的不同假设是：不要跨 ResolveStaticRequest 的 ref 参数修改整个封装，
只返回已验证的 registration/method 标量，再由调用方持有的只读值承接。
这尚未实施，也尚未证明能消除复制；必须验证实际调用方 Tier1，以及失败/解码/准入的事实传递，
不能通过跳过任何预业务检查获取成绩。只有机制成立才运行原始 A 基线的 ABBA/BAAB
及旧静态候选的配对对照，仍须保留原分配门禁与 +512 B/op 负对照。
