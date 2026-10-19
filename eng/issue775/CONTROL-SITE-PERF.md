# #775 独立 BindingSite：固定候选性能计划

固定实验驱动f82fa8685ae6da3d8d8d2df77b96fb5cc4b9d987。
由旧454f51e913684853c1caa2221bc92c94e6ad1789、锁定的control-site.patch和原样方法提取
生成确定性本地候选f4ef71e255f880659862d0e7c663e50891281228，tree
fb644a763bc19c72c9b214aa8c639928a0700f23。这是Actions中的本地源码提交，不是远程HEAD。
合并后的六文件diff SHA256为49ee87956d9463fa97ae40f85806fcb2c3d6a94187bf983ddbccb102168213cf。
首次未拆文件候选3b9aa916的旧默认维护性失败保留，不当成最终候选。

启动前证据：39252540867 的default/codegen作业完成，默认构建、单元/生成器、格式/原维护性
和两臂各443 smoke、416新增语义断言（8组）、12 persistent场景均通过。默认测试报告
commitSha读取的是CI驱动f82fa868，不是本地候选；以candidate-identity、cwd及源归档确认测试对象。
没有把报告的驱动SHA伪称为被测生产SHA。

重新采集的实际OneWay Tier1仍有完整状态机建立和site关联存储；控制块88→80，
64+32（重叠8）的拷贝变为64+16，site不再先写入控制块，入口栈帧584→568。
向量复制指令数没减少，额外site仍独立承接；不能说整个状态机缩小或所有复制消除。
这是窄的机制变化，只授权测量，不授权优化验收。源布局提取纳入同一候选，全部原方法体保留。

两个job各自同VM串行完整ABBA/BAAB，一组对原始dev1a54993d（基线不替换），
另一组对旧静态454f51e9。两个job不混池，不直接比较其绝对耗时。
original-dev组原封不动调用候选已有run.py，old-static组只给既有boundary_pair显式设定提交身份。
每组8slot、64次真实TCP/SharedMemory Unary/OneWay、720次BDN actual、32组原allocation及+512负对照。
保留原10,000预热/100,000测量、两CPU、tiering/PGO、原BDN 3launch/6warmup/15actual/4096invocation。
不带本轮语义检查overlay、sender诊断shim、JIT环境或有界诊断批次进入正式计时。
原始A仅使用既有五文件harness overlay，旧静态比较不改harness。

全部退出码、原始延迟、BDN层级、CPU、分配median/spread、drain、准备/首调独立保留。
任何失败不删样本、不放宽预算、不重跑挑数。+512负对照必须因超预算失败，不接受构建错误。
只有四对log-ratio，df=3探索性区间且无多重比较校正，不显著不证明等价；无事后3%门槛。
两个PayloadSize标签仍为同一原有OneWay表达式。结果可能不支持收益，此时保留证据而不集成候选。
PR保持Draft，#775开放，Replace/Receipt/Streaming仍不放行。
