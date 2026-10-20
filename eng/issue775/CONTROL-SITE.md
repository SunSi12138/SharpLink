# #775：独立 BindingSite 输入的单点实验

本轮检验一项假设：将 BindingSite 从通用 ResolvedCallControl 分离，
使该值从88回到80字节，并查看实际发送入口是否减少复制或仅将成本转移到额外参数。
不改变公开/generated ABI、协议、P2C、Session/token准入、重试、metadata、取消或资源所有权。
改写涉及五个客户端partial文件；为满足原行数门禁，另将原样方法体移入一个partial文件。
服务器及此前persistent正确性修复不变。

## 可复现候选，不混淆生产分支与测量源码

固定旧源码 `454f51e913684853c1caa2221bc92c94e6ad1789`。
绑定改写为 `eng/issue775/control-site.patch`，SHA256
`17b337a676e7a1f690cf5f29e844532f34602d033d679bf480336543e8043b9e`。
驱动校验该哈希、完整旧SHA、五文件白名单及默认构建的词法等价，然后执行下述机械提取。
Actions只在独立checkout应用改动，创建确定性的本地候选commit，记录tree/commit、
完整source archive、合并后的production.patch及实际测试叠加。这个本地commit不由CI推送，
不能称为远程分支HEAD。当前远程src不包含本候选改写，负结果不会遗留生产改写。

BindingSite只沿Unary/OneWay选路所需链路单独传入。两个拦截器state保存各自的readonly引用，
GetTerminalControl仍仅改metadata。每次重试仍重新选择实际连接/token，不重用首次token。
取消/遥测/超时控制继续通过原80字节结构；不新增AsyncLocal、共享可变site或跳过原检查。

## 原维护性门禁与第一次执行

首驱动1b332771，运行39250907283：codegen job success，default job failure。
默认构建、2,123单元测试、271生成器测试和格式均通过，但Invokers.cs为1131 LOC，
超过已有1100 allowance；没有调整baseline、排除该文件或将旧运行改写为成功。
该轮候选本地SHA为3b9aa916ae8f241b79690a4d1ac91e2694fb4cb8，产物保留。

后续control_site_layout.py将CompleteLogicalInvocation及其awaiter/枚举器整个连续方法块
原样移入SharpLinkClient.LogicalInvocation.cs（131行），Invokers变为1003行。
原方法块SHA256固定为2ae3aaffe3f14ce7cf131ecf42127f7e992050616e409e74056e567831e9d138；
驱动断言提取前哈希一致，原文件剩余文字及新文件中方法体逐字节一致，不增加转发函数。
它只是源码布局修正，不是新增Streaming实现。保留原默认语义、维护性规则和allowance。
后续候选身份必须同时记录布局转换脚本SHA、方法块SHA、完整六文件diff与源码tree。
布局会改变编译输入的文件位置，不能仅凭首轮JIT替代后续候选的代码生成证据。

## 先验证语义与代码生成

两组job均只读：默认候选构建/单元/生成器/格式/维护性；同机旧静态/候选静态语义与JIT。
后者依次验证真实TCP/SharedMemory × 拦截器开关 × 遥测开关的8组场景，
每组两个不同服务器及客户端，检查首次未Prepare、一次瞬态失败后的Unary重试、
非重试Unary、并发metadata隔离、OneWay业务完成、预取消与拦截器挂起后取消、调用清理。
既有443 smoke及12场景persistent回归两臂均执行，不是所有生产竞态的证明。

JIT使用既有有界发送诊断（每批4096、实际业务完成后推进），4组合×2臂，
保留真实tiering/PGO、2CPU、全部listing、原始诊断数组。原探针中依赖旧BindingSite字段的
control微循环断言对两臂一起移除，保存叠加文件及hash。这不是未修改的canonical工作负载；
不生成ABBA/BDN性能summary，不把JIT进程耗时或局部字节数用于验收。

代码生成门槛不只是ControlBytes=80：要审阅实际Tier1发送入口、状态机字段搬运和生成代理，
核对是否因额外参数增加spill。若只是从结构改成同量或更多搬运，保留负证据；
若机制可见改善，也必须另行固定被测源码对原始dev及旧静态作ABBA/BAAB，
保留原allocation预算及+512负对照后再判断收益。未批准等价边界，不事后发明3%阈值。

PR保持Draft，#775开放。Replace、Receipt、Streaming不在本实验范围，不自动合并或授权第二阶段。
