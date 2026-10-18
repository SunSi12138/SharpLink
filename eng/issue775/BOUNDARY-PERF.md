# 固定候选4e16bf4c的性能复验

本轮预先固定被测源码，不将后来只含诊断脚本的driver提交冒充被测源码：

- 新静态候选：4e16bf4c7ae18edf206f3e9bf1eb369c549f860e。
- 原始dev：1a54993d3550911f5979a7b4d3f9f5f63ee86a64，原基线不能替换。
- 旧静态：a473f5d3a50d3b23874f8b1bb72f46e65ff939d1，仅用于归因比较。

开始前已验证：39089339116默认/实验回归通过；39089339152真实Zstd八场景新版本通过、
旧版无metadata四场景失败；39089339077代码生成覆盖完成。独立检查实际OneWay Tier1：
旧TCP IG43/44的96字节往返及IG45的BoundMethod写回，候选已改成直接从方法对象取Descriptor；
SharedMemory同样不再执行目标往返。Unary仍有复制，不以整体代码大小宣称吞吐收益。
这只授权性能实验，不授权第二阶段或合并。

两个独立job，每个job内部在同一VM上完整串行ABBA+BAAB：
original-dev job原封不动调用已有run.py；old-static job两边均绑定开启，使用boundary_pair.py。
两job之间不混池、不直接比较绝对时间；每个比较各自有同期对照。
每job8slot、64次真实传输场景、720次BDN actual、32组原allocation门禁和一次+512负对照。
保留全部原workload、10,000预热/100,000测量、两CPU、原tiering/PGO和BDN 3×6×15、4096invocations。
未启用JIT输出、采样器、诊断批次或compression-test overlay。原基线仅叠加原有五文件harness。
旧静态比较不叠加任何harness；两个源码中的原五文件已一致。

原allocation runner、median/spread预算、canonical CPU配置不变。失败不重跑挑数；
脚本错误修复与新测量须明确区分，不改写旧失败。负对照必须因分配超预算而失败。
四对log-ratio探索性95%t区间未作多重比较校正；未批准等价边界，不显著不能证明等价。
两个BDN PayloadSize标签还是同一个OneWay表达式，不是不同真实payload。

结果尚待本轮实际运行和审阅。当前生产限制不因代码生成成功消失：静态固定endpoint、
无动态Replace/Receipt/Streaming，重连自动准备、生产协商等仍不实现。
压缩回归仅覆盖8KiB同步解码路径，不覆盖persistent大请求解码等额外路径。
