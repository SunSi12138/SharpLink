# 固定8f1a81e2的方法形态预计算性能复验

本文件在计时前冻结范围和源码。候选8f1a81e2c3bfc2c0cfc4734b658684631aa2954e，
原始dev为1a54993d3550911f5979a7b4d3f9f5f63ee86a64，前一静态版本为8810547756d1c16367bba8d81f6707018b9d7e5d。
后续driver提交只含本计划、只读workflow和16行身份包装，不冒充被测源码。

已满足启动条件：39126508208默认/实验构建及测试success；39126508314真实persistent回归success；
39126508404代码生成成功。候选smoke保留443项，两个传输合计2,621,440次进程内谓词对照、
8,192个generated绑定表flag组合通过。这不是上百万网络请求。

实际完整Tier1：TCP OneWay DispatchOneWayRpc的IG41，从描述符字段加载、Kind/stream检查、
两次独立flag等价测试，减少为movzx/and 192/读取RequiredRequestFlags/cmp/jne；
SharedMemory OneWay和两个Unary调用方同样保留内联并执行掩码比较。token和MethodId查找仍在。
独立Resolve的514→423字节只能辅助说明，不当作收益；不能说整个派发不再读取descriptor，
通过形态检查后实际业务仍需要descriptor。codegen ZIP sha256:
c974747dd90e9d90477bb7a4721f8056ba9160ffeea48bffd7c7846e930234e9。

两个独立job各自在同一VM完整串行ABBA+BAAB，不跨job合并绝对值：
original-dev原封不动调用candidate中的run.py；previous-static使用shape_pair.py显式设置
未修改boundary_pair.py的两臂身份，两个静态版本均开启绑定且保留persistent修复。
原始基线不能用前一静态版本替代。没有采样器/JIT/诊断批次/压缩测试overlay进入计时。
每job8slot、64次RPC场景、720次BDNactual、32组canonical allocation与一次+512负对照。
原五文件harness、workload、10,000预热/100,000测量、2CPU、tiering/PGO、
BDN3launch×6warmup×15actual、4096invocations不变。预算与canonical CPU配置不变。
不利样本/失败保留，不重试挑数、不调预算、不重定原始基线。

Bind与首调用单列，冷路径新整数存储/构造成本不能用稳态收益掩盖。
报告sender/receiver边界、吞吐、p50/p99/p99.9、CPU、分配和drain，不以发送排队当业务完成。
BDN两个PayloadSize标签仍是同一实际OneWay表达式，不是独立业务负载。
四对log-ratio探索性t区间df=3，未多重比较校正、没有批准的等价边界；不显著不等于等价。
局部机器码改进不授权第二阶段；结果须由实际测量和审阅补充。PR保持Draft，#775开放。
