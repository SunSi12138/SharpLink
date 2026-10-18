# #775：Bind 冷路径固定不可变方法形态

本轮只改变 StaticBindingTable.cs 的实验编译路径，旧对照固定8810547756d1c16367bba8d81f6707018b9d7e5d。
保留已经通过回归的persistent方法引用修复，不撤回正确性改动，也不扩大动态/Streaming范围。

旧Resolve每次从32字节descriptor判断Kind、HasClientStreams、ClientStreamCount、HasResponsePayload。
这些信息属于注册固定的方法事实，在Bind构造StaticBindingMethod时可预计算为RequiredRequestFlags。
支持静态调用时为OneWay/HasReturn的期望位组合；不支持时为-1，任何masked byte都无法匹配。
逐请求仍验证实际Session中的完整token、MethodId，并比较请求两项形态位，其他flag仍由原管线检查。
未知token/未知method/形态失败的顺序、registration输出、容量和关闭逻辑完全不变。
不是把Bind当作授权缓存，不省略每次请求准入，不按当前注册重新解释请求。

StaticBindingMethod仍是internal sealed record，Descriptor/SupportsCancellation/RequiredRequestFlags均get-only。
不能用with或对象初始化器独立换Descriptor而不更新派生状态；空with克隆仍合法且值等价。
不改变generated ABI、公共API、原始benchmark、budget、runtime参数或框架请求封装布局。
Bind对象冷路径多一个整数的存储成本需要计入，不承诺冷启动或所有场景均变快。

## 语义对照

StaticBindingShapeChecks独立复制旧形态谓词：每个传输smoke运行5,120个描述符×全部256种flag，
合计1,310,720次谓词对照。覆盖全部256种Kind值、HasResponsePayload/HasClientStreams的四组合，
ClientStreamCount=-1/0/1/2/int.MaxValue；其他描述符字段和原始对象不变。
这些是进程内组合测试，不是上百万条真实网络请求。
另外在真实StaticBindingTable安装generated契约，逐方法×全部flag比较原谓词结果、精确方法/注册对象，
并验证错误incarnation、未知method和Close仍拒绝。原443项真实Session/reader检查不删除。
表级测试并不承诺Error等所有flag组合可通过外层frame校验，也不修改该外层校验。

## 代码生成与后续决策

只读issue-775-shape.yml显式标记触发。与旧对照同一VM/SDK/2CPU/原tiering/PGO，
复用已修正的有界BoundaryDiagnostics，不重演无限连续OneWay发送触发队列上限。
所有原始JIT、Tier1覆盖、命令、环境、overlay、二进制hash保留，诊断耗时不混入性能计时。
运行成功和Tier1 header只表示执行/覆盖，人工检查实际调用方后才能判断机制。

机制成立也不自动授权第二阶段。随后候选应分别与原始dev 1a54993d和当前旧静态88105477
做同机固定源码ABBA/BAAB；两个比较不跨VM混池。原workload、allocation median/spread和+512负对照保持。
不新增事后3%阈值，不用不显著宣称等价，不以单个有利场景代替整体判定。
当前未有此候选的.NET或性能结果；以实际Actions及审计补充为准。PR保持Draft，#775开放。
