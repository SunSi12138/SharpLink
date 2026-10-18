# 静态 OneWay：发送线程 CPU 与队列诊断

## 固定范围

A固定原始dev `1a54993d3550911f5979a7b4d3f9f5f63ee86a64`；
B固定当前静态 `2d98242c66f43347dd1b7413a47fadc86cc88bda`，包含既有正确性修复和形态预计算。
驱动提交另行记录。仓库中只新增eng诊断文件和只读workflow，不修改生产src、公共API、
原始benchmark、分配预算、协议或原有安全规则。#862保持Draft，#775不关闭，不进入第二阶段。

## 预先冻结的实验

同一job/VM顺序 `A B B A B A A B`，每slot运行TCP/SharedMemory × c1/p1、c8/p4。
每场景独立进程，使用既有generated `IBenchmarkRpc.PublishEventAsync`；两个固定CPU、
ProcessorCount=2，保持TieredCompilation/TieredPGO=1。先32批预热，再256批记录，每批4096调用。
每次调用携带唯一序号、固定ticks/tag，receiver检查值和去重；每批全部进入并完成业务后才下一批。
握手、Prepare、首次小Unary、序号数组分配不在测量窗口。末尾检查小Unary、Bind次数和admission reservation。

使用固定数量的专用发送线程，不是原BDN或既有异步harness的调度方式；这是有界闭环诊断，
**不直接解释成开放环吞吐、BDN成绩、生产验收或恢复基线**。本地完成事件不是线上ACK/Receipt。
服务端业务事件先于框架清理完全结束，指定检查不能证明所有服务器资源均已归零。

每个发送线程每批在实际调用循环前后读取Linux `CLOCK_THREAD_CPUTIME_ID`；协调线程在整个
稳态窗口前后读取 `CLOCK_PROCESS_CPUTIME_ID`。同时保留Stopwatch绝对时间戳、线程ID、
每线程完成数、异步完成数和队列gauge。没有每调用新增时间戳/事件/系统调用。
CPU系统调用本身有开销，包含在边界附近，不通过事后估计扣减。没有硬件周期/指令级样本。

sender CPU仅指这些发送循环；如果它们阻塞后在其他线程执行continuation，那些CPU计入
其余进程而非发送线程。其余CPU含客户端发送泵、服务端、GC/JIT、协调与sender同步，
**不能标注为纯server CPU**。队列在每个sender结束和整批sender完成时采样，不是连续高水位。

控制解析与选路/保留/释放另以100,000预热、1,000,000次微循环诊断；调用真实私有方法，
不发送业务包、不改验证逻辑。测试shim只在独立checkout追加partial类；不修改现有方法源码。
记录ResolvedCallControl实际尺寸。微循环有独立内联上下文及验证开销，不能从RPC总成本相减，
更不能把它们当作跳过准入的快速替代实现。

A只叠加此前五文件harness加本次诊断；B只叠加本次诊断。完整overlay及hash保留。
两个产物的每个DLL在执行前后核对；已有src文件不得被overlay修改。
诊断计时后另起8个JIT进程（A/B各4场景），相同有界工作量，无采样器混入计时。
Client/Connection/StaticBindingRpcChannel的完整原始listing保留；coverage只记录header，
不完整/交错/只有Tier0不能当作实际优化后派发证明。不通过关闭内联/PGO强造热点。

## 判读与失败规则

32个计时场景，共33,554,432次稳态OneWay，另有预热；计时原始sender/batch记录全部保留。
4对样本，报告中位数比和探索性配对log-t区间（df=3），未多重比较校正；不显著不证明等价。
只用于排查发送CPU、调度等待和排队的关联，不从单一指标断言因果。
失败/超时不删除、不重跑挑数。没有本轮canonical allocation或+512负对照成绩：
原始预算及验证入口未变，但不能用诊断分配替代其验收。
先提交固定诊断，得到结果再选择一个有证据支持的生产优化点，而非多点同时修改。

复现：

```sh
python3 -m unittest discover -s eng/issue775 -p test_client_send.py -v
python3 eng/issue775/client_send.py --base /path/A --candidate /path/B --driver "$PWD" --output /path/new-output
python3 eng/issue775/client_send_report.py /path/new-output
```

工具语义：Linux man-pages `clock_gettime(3)` 的process/thread CPU clocks；
https://man7.org/linux/man-pages/man3/clock_gettime.3.html
.NET Release JIT输出仍依照既有类名过滤，不使用Debug/Checked-only汇编过滤。
