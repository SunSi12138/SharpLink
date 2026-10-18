# 固定候选4e16bf4c的性能复验

## 输入与预先确定的计划

被测源码与后续修复/文档提交分开记录：

- 新静态候选：`4e16bf4c7ae18edf206f3e9bf1eb369c549f860e`。
- 原始 dev：`1a54993d3550911f5979a7b4d3f9f5f63ee86a64`，原基线不能替换。
- 旧静态：`a473f5d3a50d3b23874f8b1bb72f46e65ff939d1`，只用于归因比较。
- 性能驱动：`2763bb4276a6f715e9dfd4827450e7285f919363`。

运行前已完成默认/实验回归39089339116、8KiB真实Zstd回归39089339152及代码生成39089339077。
实际OneWay Tier1中，旧TCP IG43/44的96字节往返及IG45的BoundMethod写回被直接消费方法对象替代，
SharedMemory也消除了目标往返。Unary仍有复制。这只支持开展性能实验，不代表性能收益。

性能运行：https://github.com/SunSi12138/SharpLink/actions/runs/39089871063

两个独立job，各自在同一VM完整串行ABBA+BAAB；不跨job混池或比较绝对耗时。
original-dev job原封不动调用已有run.py；old-static job两边均绑定开启，使用boundary_pair.py。
每job8slot、64次真实TCP/SharedMemory×Unary/OneWay×c1p1/c8p4、720次BDN actual、
32组原allocation门禁和一次+512 B/op负对照。每RPC进程10,000预热/100,000测量，
两CPU、原tiering/PGO，BDN仍3 launches×6 warmup×15 actual、4096 invocations。
无JIT输出/采样器/诊断批次/压缩测试overlay进入计时。原基线只叠加原有五文件harness；
旧静态比较不叠加harness。原allocation runner、median/spread预算及canonical CPU配置不变。

## 审阅结果（2026-10-18）：不放行第二阶段

两job执行及分配门禁均通过，但没有证明整体收益或恢复原始基线。
独立只读校验重新计算了两份产物的128次RPC、2,560万条发送/接收延迟值、1,440次BDN actual、
64组allocation与两个负对照，核对顺序、来源SHA/协议、分位数、完成边界、BDN launch层级和原始分配样本。
重建数值与保存的summary一致；没有重跑计时、删除样本、放宽预算或重定基线。

下表为业务完成吞吐的各臂四slot中位数之比。两列来自各自同期对照，不相互相除：

| 场景 | 新/原始dev | 新/旧静态 |
|---|---:|---:|
| TCP Unary c1/p1 | 1.0296 | 0.9895 |
| TCP Unary c8/p4 | 0.9845 | 1.0647 |
| TCP OneWay c1/p1 | 0.9995 | 1.0392 |
| TCP OneWay c8/p4 | 0.9753 | 1.0008 |
| SharedMemory Unary c1/p1 | 1.0124 | 1.0392 |
| SharedMemory Unary c8/p4 | 0.9873 | 1.0062 |
| SharedMemory OneWay c1/p1 | 1.0398 | 0.9544 |
| SharedMemory OneWay c8/p4 | 1.0427 | 0.8712 |

重要不利信号：

- 相对原始dev，原有OneWay BDN发送端耗时：PayloadSize=16为2729.11→2810.92 ns/op，
  比值1.02998（+3.00%），配对几何比探索性95%区间1.00347–1.07472；
  PayloadSize=256为2721.81→2859.76 ns/op，比值1.05068（+5.07%），区间1.00459–1.09003。
- 相对旧静态，SharedMemory OneWay c8/p4业务完成吞吐187399.53→163261.43 ops/s，
  比值0.87119（-12.88%），区间0.80513–0.96542；进程CPU时间比1.15172，区间1.04132–1.22767。
  这是旧静态对照，不是原始dev对照，不能混淆。
- 相对原始dev，TCP OneWay c8/p4接收p99比1.29418，区间0.62388–2.12766，
  点估计不利但区间宽，不能忽略也不能夸大确定性。
- 新/旧静态的BDN两标签耗时比1.02268/0.98424，区间0.88831–1.11212/0.95768–1.01931，
  未见一致发送端收益。两PayloadSize标签仍是同一个原有OneWay表达式，不是不同真实payload。

原始dev比较的+512负对照median为1862.232 B/op；旧静态比较为1865.286 B/op，
均因超过原1450 B/op预算而失败，而非构建/超时失败。其余64组原median/spread门禁通过。
完整尾延迟、CPU、分配、cold Prepare/首调与所有区间保留在各自summary.json及raw记录。

四对log-ratio探索性95%t区间（df=3）未作多重比较校正；中位数比与配对几何比不是同一统计量。
未批准等价边界，不显著不能证明等价，也不使用事后“约3%”门槛。
这些数据不证明消除复制本身造成了某个回退，也不证明绑定架构无法优化；它们不支持扩大实现范围。

## 原始产物

- 原始dev比较：artifact `12122841316`，ZIP SHA256
  `64fc576b3b71c15c93ec4864e53e017ec91d3325dc59aaaae35bdb6ad09da82b`。
- 旧静态比较：artifact `12122456680`，ZIP SHA256
  `1357c074aa2055eb33954a0c04527e79c8120562bc6a192198837de21fbfd623`。
- OneWay代码生成：artifact `12121214839`，ZIP SHA256
  `1753db7dc6c60cb60a1a23b1521cc65f54fe6f45f1d3efe0c2e8a37a2318b876`。

## 后续正确性修复不冒充已测性能候选

审阅发现4e16仍存在persistent大请求解压后丢失BoundMethod的问题。
`628a2f44014d899c25c1e94ecf59e0575602c2c1`保存并恢复解压前已验证的方法引用；
`fb1565c66eb3bd272428d3f0275fd1e3fd9652a4`仅修正专项测试缺少SDK命名空间引用。
修复后的真实12场景及旧版4个失败已复现，见 `PERSISTENT.md`。
这里的性能结果只属于4e16，不是628a/fb1565或后续文档HEAD的重新验收。
生产限制仍为静态固定endpoint，不扩展Replace/Receipt/Streaming；PR保持Draft，#775开放。
