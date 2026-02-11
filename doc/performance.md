# SharpLink 性能分析

本文档基于当前 `src/` 代码实现，对框架的性能设计、现存瓶颈、内存分配热点、改进方向与当前局限进行系统梳理。

## 1. 已使用的性能优化手段

### 1.1 协议与编码层

- 固定头部二进制协议（`Magic + Length + Type + Flags + RequestId`），避免文本协议解析开销。
- `PacketHelper.TryReadMessage` 使用 `ReadOnlySequence<byte>` 做零拷贝切片，尽量减少 payload 复制。
- 请求头中的 `interfaceHash + methodHash` 使用 `long`，服务端可直接哈希路由，避免字符串匹配。
- `PacketFlags.IsOneWay` 避免 oneway 调用产生 response 往返。
- `PacketFlags.IsCancellable` 仅在“方法显式声明 `CancellationToken` 且调用端 token 可取消”时置位，避免普通请求进入取消路径。
- 协议层支持 `Cancel` 包，减少超时等待和无效计算占用。

### 1.2 发送路径

- `BufferWriterPool` 复用 `ArrayBufferWriter<byte>`，减少每次请求的 writer 分配。
- `RpcSession` 内部发送队列（`Channel<ArrayBufferWriter<byte>>`）串行写出，降低多线程并发写 socket 的锁竞争。
- 发送侧有简易批量 flush 策略（4KB 或 1ms 窗口），在吞吐和延迟之间做折中。
- TCP 传输默认 `NoDelay = true`，减少小包延迟。

### 1.3 调用与任务模型

- 对外 API 大量使用 `ValueTask`，降低高频调用场景中的 `Task` 分配。
- `RequestManager` 使用固定环形槽位（数组 + `Interlocked`）管理请求，不走字典查找。
- `RpcRequestOperation<T>` 基于 `ManualResetValueTaskSourceCore<T>` + 对象池复用，降低每次请求 await 对象分配。

### 1.4 生成器优化

- 生成器按调用类型生成专门入口（无参数/普通/oneway/流式），避免 runtime 单入口多分支。
- blittable 参数支持批量写入固定区（`Unsafe.WriteUnaligned`），减少序列化器介入。
- blittable 类型尺寸按类型复用静态字段，避免重复 size 变量。
- Stub 端方法分发使用 `switch(methodHash)`，避免反射调用。
- proxy和stub类型由生成器生成静态注册表，避免反射。

### 1.5 数据结构与并发

- 服务注册使用 `FrozenDictionary<long, ...>`，读路径低开销。
- 流管理使用 `(requestId, streamId)` 键，支持多流并发复用。

### 1.6 协议层使用 `PacketFlags.IsCancellable`。

- 仅当 `IService` 接口方法显式定义 `CancellationToken` 且调用 token `CanBeCanceled=true` 时，客户端才置位该 flag 并注册取消回调。
- 服务端仅在该 flag 置位时创建 linked CTS 并进入 `requestCancellationMap`；否则直接透传 `serverLoopToken` 给 stub。
- `IService` 方法上的 `CancellationToken` 参数被限制为“最多一个”，由编译期诊断 `SHARPLINK002` 报错。


### 1.7 BufferWriter 池上限和缩容减少内存占用

- 池化增加上限：`MaxPooledWriters = 512`。。
- 容量回收阈值：`MaxRetainedCapacityBytes = 64KB`，超过则直接丢弃不回池。
- 支持配置入口：`BufferWriterPool.Configure(...)`，并可在 Builder 侧通过 `UseBufferWriterPool(...)` 配置。
- TODO：可引入分级池（small/medium/large）。
- 常规对象仍复用，峰值大对象不会长期滞留在池中。


## 2. 当前主要性能问题（含内存分配热点）

以下问题是“当前仍影响吞吐、延迟、分配”的核心项。

## P0

### 2.1 服务端请求分发仍是“每请求一个 async 状态机”

现状：
- `DispatchRpcAsync` 为 `async Task`，每个请求创建任务状态机。

影响：
- 高频小包场景下调度与状态机分配占比明显。

建议：
- 将快路径改为可同步完成的 `ValueTask` 形式。
- 对纯同步/快速返回方法，避免不必要的 `await` 链。

### 2.2 流调用路径存在固定额外分配

现状：
- Proxy 对有流参数的方法总是 `new List<Task>() + Task.WhenAll(...)`。
- `InvokeServerStreamCoreAsync` 每次都会 `Channel.CreateUnbounded<T>()` + `Task.Run(...)`。

影响：
- 每次流调用都产生固定分配和调度成本。

建议：
- 生成器对“流参数数量 1/2/3”生成专门代码，避免 `List<Task>`。
- 可选去掉 `Task.Run`，将发送启动逻辑改成更轻量的异步泵。

## P1

### 2.3 blittable 解码跨段回退会分配临时数组

现状：
- 生成 Stub 在 `reader.UnreadSpan` 不连续时 `new byte[size]` 临时拷贝。

影响：
- 虽是回退路径，但在特定网络分片模式下会频繁发生。

建议：
- 使用 `stackalloc`（小尺寸）+ `TryCopyTo`，超过阈值再租 `ArrayPool<byte>`。


### 2.4 StreamManager 使用 `ConcurrentDictionary` 全路径

现状：
- 每个流 chunk 都有并发字典查找与 key 构造。

影响：
- 小包高频场景下有原子与哈希开销。

建议：
- request 内部先定位一次，再用 streamId 到数组/小字典。
- 对单流默认路径（streamId=0）做专门快路径。

## P2

### 2.5 发送队列 `Channel<T>` 自身节点分配

现状：
- 每个待发送包入 `Channel` 会产生队列节点与调度成本。

建议：
- 极致性能模式可考虑自研无锁 ring queue。
- 或保持 `Channel`，以可维护性优先（推荐短期不改）。

### 2.6 `PacketHelper` 头部 `CopyTo(stackalloc)` 每包固定拷贝

现状：
- 每个包都复制 15 字节头到栈。

影响：
- 单次很小，但极高 PPS 时可见。

建议：
- 可改为 `SequenceReader<byte>` 直接读头字段。
- 当前收益较小，优先级低。

### 2.7 错误路径字符串化协议

现状：
- 错误多通过字符串传输。

影响：
- 错误高发时会有额外编码/分配。

建议：
- 统一错误码 + 可选 message，减少大字符串。

## 3. 生成代码层面的分配与可优化点

### 3.1 可优化点

- 对非流、非复杂参数方法，生成“完全无闭包”调用代码。
- 对同步可完成的 `ValueTask` 路径，减少 `async/await` 状态机。

### 3.2 当前做不到完全消除的分配

- `IAsyncEnumerable<T>` 本身枚举器状态机分配（尤其用户侧迭代器）。
- 引用类型参数的序列化/反序列化分配（取决于 serializer 与数据模型）。
- 网络层/管道层在系统调用边界的必要缓冲成本。

## 4. 局限与不可避免成本

- 纯托管 + 通用序列化框架下，不可能在所有类型上实现零分配。
- 跨平台传输抽象（Socket/NamedPipe/AnonymousPipe）带来最低公共实现成本，极限性能会输给专用实现。
- 泛型 RPC + 动态服务组合在易用性和极致性能间存在天然权衡。
- 取消、超时、流式多路复用等能力会引入额外状态管理开销，这部分属于“可控成本”，不可完全消除。

## 5. 建议的落地顺序（按投入产出比）

1. 优化流发送生成代码：去 `List<Task>`，按流参数个数展开。
2. BufferWriter 池增加上限和大对象剔除策略。
3. Stub 跨段 blittable 回退改为 `stackalloc/ArrayPool`。
4. 评估 StreamManager 快路径与队列结构替换收益。

## 6. 需要补充的性能验证

- 故障：断连、取消、超时。
- 指标：
- 吞吐（ops/s）
- P50/P95/P99 延迟
- 每请求分配（B/op）
- GC 次数与暂停时间

增加`BenchmarkDotNet` + CI 回归基线，避免优化回退。
