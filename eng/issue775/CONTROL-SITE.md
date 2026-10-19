# #775：独立 BindingSite 输入的单点实验

本轮仅检验一项假设：将 BindingSite 从通用 ResolvedCallControl 分离，
使该值从88回到80字节，并查看实际发送入口是否减少复制或仅将成本转移到额外参数。
不改变公开/generated ABI、协议、P2C、Session/token准入、重试、metadata、取消或资源所有权。
单点改写涉及五个客户端partial文件；服务器及此前persistent正确性修复不变。

## 可复现候选，不混淆生产分支与测量源码

固定旧源码 `454f51e913684853c1caa2221bc92c94e6ad1789`。
改写提交为 `eng/issue775/control-site.patch`，SHA256
`17b337a676e7a1f690cf5f29e844532f34602d033d679bf480336543e8043b9e`。
实验驱动必须校验该哈希和完整旧SHA、五文件白名单与默认构建的词法等价。
Actions只在独立checkout应用补丁，创建确定性的本地候选commit，记录tree/commit、
完整source archive及实际叠加的测试文件。这个本地commit不由CI推送，也不能称为远程分支HEAD。
当前分支src不包含本候选改写；读写隔离使负结果不遗留未经验证的生产改写。

BindingSite只沿Unary/OneWay选路所需链路单独传入。两个拦截器state保存各自的readonly引用，
GetTerminalControl仍仅改metadata。每次重试仍重新选择实际连接/token；不将首次token用于后续连接。
取消/遥测/超时控制继续通过原80字节结构；不新增AsyncLocal、共享可变site或跳过原检查。

## 先验证语义与代码生成

两组job均只读：默认候选构建/单元/生成器/格式/维护性；同机旧静态/候选静态语义与JIT。
后者依次验证真实TCP/SharedMemory × 拦截器开关 × 遥测开关的8组场景，
每组两个不同服务器及客户端，检查首次未Prepare、一次瞬态失败后的Unary重试、
非重试Unary、并发metadata隔离、OneWay业务完成、预取消与拦截器挂起后取消、调用清理。
既有443 smoke及12场景persistent回归两臂均执行；不是所有生产竞态的证明。

JIT使用既有有界发送诊断（每批4096、实际业务完成后推进），4组合×2臂，
保留真实tiering/PGO、2CPU、全部listing、原始诊断数组。原探针中依赖旧BindingSite字段的
control微循环断言对两臂一起移除，保存叠加文件及hash。这不是未修改的canonical计时工作负载；
不生成ABBA/BDN性能summary，不把JIT进程耗时或局部字节数用于验收。

代码生成门槛不只是ControlBytes=80：还要审阅实际Tier1发送入口、状态机字段搬运和生成代理，
核对是否因额外参数增加spill。若只是从结构改成同量或更多搬运，保留负证据并停止此候选；
若机制可见改善，也必须另行固定被测源码对原始dev及旧静态作ABBA/BAAB，
保留原allocation预算及+512负对照后再判断收益。未批准等价边界，不事后发明3%阈值。

PR保持Draft，#775开放。Replace、Receipt、Streaming不在本实验范围；不自动合并或授权第二阶段。
