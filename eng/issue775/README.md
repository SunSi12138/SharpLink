# #775：静态 Session Binding 最小原型

这是决策用的实验，不是生产协议实现。默认构建保留 dev 的原有请求语法；只有显式设置
`SharpLinkStaticBindingPrototype=true` 才启用实验路径。实验包禁止 Pack，Prepare API 仅供内部基准使用。

## 范围与 wire

仅支持固定 endpoint 上的静态注册、Unary 和 OneWay，以及该 endpoint 的连接池。
不实现动态 Replace/Unregister、Receipt、Streaming、Cluster、重连后的自动 Prepare、slot 回收或生产兼容协商。
现有业务调用的容量、取消、interceptor 和调用生命周期管线继续使用；Binding 不是授权缓存。

实验采用独立 magic `0xB7`，与普通 dev 的 `0x89` 隔离，不能混连；这不是已分配的生产协议版本。
请求路由前缀为 little-endian `BindingId:u64 + MethodId:u64`，恰好 16 字节。
它不包含 15 字节 frame header、元数据、业务参数、TCP/IP 或 SharedMemory 开销。
原始 dev 基线同样是 16 字节前缀（ContractId + MethodId）；本实验测试增加正确身份绑定后能否保持该基线。

Bind 使用真实数据 Session 上的控制帧 240，body 为 ContractId:i64 与完整 RpcAssemblyHash:128，共 24 字节。
Ack 为帧 241，body 共 40 字节，包含状态、ContractId、BindingId、RegistrationId 和 registry generation。
控制 operation id 与业务 PendingCall 表分离。长度、保留位、标志、成功/失败字段以及未知操作都会校验。
成功 Ack 只在服务端表项可见后发送；Bind 不调用业务 codec，也不激活服务。

每 Session 最多 64 个绑定、每绑定最多 256 个方法；客户端完成缓存和在途操作受同一容量约束。
高 32 位 incarnation 固定为 1，低 32 位是从 1 开始的 slot；Session 生命周期内不回收 slot。
服务端验证完整 token，而不是只使用低位索引。断开清表，不跨 Session 转移表项。

## 调用顺序

`Get<T>()` 只检查本地 generated 注册并创建代理，不做网络等待。连接就绪后显式调用内部
`PrepareStaticBindingAsync<T>(CancellationToken)`。同 Session、同 contract/hash 的并发 Prepare 合并 Bind；
单个 waiter 取消不取消共享操作，操作自身有 10 秒超时和 Session lifetime 约束。

Prepare 只为实际 ready 且 manifest 完整 hash 匹配的 Session 建立路由。发布时检查连接/manifest 快照未改变。
热路径保留固定 endpoint 的 P2C 选路，从不可变快照中一起选择 `(ClientConnection, BindingId)`，
再在同一连接上保留 admission；重定向不能携带原 token，未绑定时在序列化前失败。

服务端从实际连接的 bounded 表取精确静态 registration 和冻结的方法事实。
未知绑定、未知方法、冲突形态或流形态在解压/业务反序列化之前拒绝，不回退到 ContractId 查找。
已接纳业务仍经过原有容量、上下文、调用和清理流程。OneWay 没有新增 ACK，也不宣称 exactly-once 语义。

## 已实现的测试

`--static-binding-smoke` 使用真实 loopback TCP 与真实 SharedMemory，每种传输四个物理 Session。
检查本地 Get/未 Prepare、32 个并发 Prepare 合并、完整 hash 错误、相同数值 token 的 Session 局部性、
错误 incarnation、未知 method 的预解码拒绝、拒绝后 Session 仍可调用、流 producer 未启动、预取消、
1,024 次 OneWay 的业务接收计数以及 Stop 后旧代理/旧绑定失效。
另有控制帧分段/长度检查与表容量、关闭、slot 范围、形态检查。

这不是完整 #775 验收：尚未覆盖生产 fuzz、所有取消/超时竞态、动态服务、Receipt、Streaming、
全拓扑重连、公网部署、不同进程或 NativeAOT。测试中去重计数只验证该次测量没有漏发/重复，不构成传输保证。

## 构建与单独检查

```sh
dotnet build test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release \
  -p:SharpLinkStaticBindingPrototype=true
dotnet test/SharpLink.Benchmarks/bin/Release/net10.0/SharpLink.Benchmarks.dll --static-binding-smoke
```

切回默认构建时先 clean，避免复用另一种条件编译输出。完整实验使用两份独立 checkout。

## 同机 ABBA / BAAB

原始 A 固定为 `1a54993d3550911f5979a7b4d3f9f5f63ee86a64`，不是 #861 的 identity-header 提交。
B 是明确传入的完整 candidate SHA。开始前必须都是干净 checkout；SDK、依赖、既有 benchmark、
allocation runner 与预算必须相同。原始 A 仅叠加审计过的测试 harness，不修改其生产源码或分配策略。
叠加文件、SHA256、diff、两臂输出程序集 SHA256、SDK/runtime/CPU/环境/命令/退出码都进入 artifact。

```sh
# base/ 与 head/ 分别 checkout 上述 A 与待测 B；不要在工作区混合构建两种协议。
python3 head/eng/issue775/run.py --base-root base --head-root head \
  --expected-head "$(git -C head rev-parse HEAD)" --output evidence
python3 head/eng/issue775/summarize.py evidence
```

全部 slot 在一个 Actions job 的同一台 VM 上串行执行 `A B B A B A A B`。
每 slot 包含 TCP/SharedMemory × Unary/OneWay × (并发 1 / pool 1、并发 8 / pool 4)，
每个独立进程预热 10,000 次后测量 100,000 次。Prepare 和首调单独报告，不混进稳态。
计时进程统一固定两个可用 CPU、ProcessorCount=2、TieredCompilation=1、TieredPGO=1。
原始 allocation gate 使用 runner 的原始 CPU/环境，不用计时进程的双核限制代替 canonical 配置。

另外保留原有 `UnaryBenchmarks.Rpc_OnewayPublish` 的 BDN 发送端基准：
3 launches × 6 warmup × 15 actual iterations，invocationCount=4096；
先求每 launch 的 actual median，再求每 slot 的三 launch median，最后比较四个 slot。
其中 PayloadSize=16/256 是原有参数标签，该方法并不使用此参数，不能解释成两种实际 OneWay payload。
BDN 子进程必须匹配实验 magic，否则失败，不能把条件编译丢失后的普通 dev 当成 B。

OneWay 同时报告发送方排队完成和接收方业务完成吞吐、原始延迟、p50/p99/p99.9 与 drain。
业务参数携带 Stopwatch 时间戳；本地 receiver 数组记录每次业务执行，未新增线上的 Receipt/ACK。
Unary 使用调用响应完成边界。CPU 与 GC 分配是同进程客户端加服务端整体数据，不是分离的单端数据。
探针及其开销在两臂一致；这是同进程、真实传输的对照，不是纯网络带宽测试。

原有 allocation median/spread 预算完全不变，每 slot 都执行，最后执行 +512 B/op 负对照。
任何失败保留并报告，不删掉失败 slot、不重试挑数、不放宽预算、不重定基线。
原始延迟数组、每个 BDN actual iteration 和负对照 JSON 都随 artifact 保留。

四对相邻 A/B 的 log-ratio 给出探索性 95% t 区间（df=3），假定配对近似独立且 log-ratio 近似正态；
没有多重比较校正，不能用“不显著”证明等价，也不能忽略共享 runner 噪声或尾延迟回退。

## 决策边界

编译通过、安全测试通过、实验执行完成、分配门禁通过以及性能收益是不同结论。
汇总不会自动授权第二阶段。应先审阅原始吞吐、尾延迟、CPU、分配 median/spread 和完整性证据，
确认静态路径确实恢复基线并有值得投入的收益；证据不充分或门禁失败时保持原型，不推进生产实现。
