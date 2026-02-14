# SharpLink 性能治理总表（全量）

本文档面向“当前版本较前几版性能下降”的问题，覆盖 `src/` 全链路可优化点，并给出优先级与落地顺序。

## 1. 当前回退的高概率根因（先看这里）

基于最新代码（重点是 `src/SharpLink.Client/SharpLinkClient.cs`）分析，性能回退最可能来自以下组合开销：

2. 服务端每个 `RpcCall` 仍可能触发额外调度开销
- `src/SharpLink.Server/SharpLinkServer.cs:139` 当前是 `_ = DispatchRpcAsync(...)`，每次调用进入异步状态机。
- 高并发下会产生大量任务调度与状态机成本。

3. 流式路径固定分配仍偏多
- `Channel.CreateUnbounded<T>()`（客户端、生成器 stub）每个流调用都分配。
- 生成器为流参数和流返回生成的路径仍含 `Task.Run`。

## 2. 全链路性能问题清单（按模块）

## 2.1 Client（`src/SharpLink.Client`）

### P0

2. `ConcurrentDictionary` 用于本地取消与服务端流请求跟踪
- 位置：`src/SharpLink.Client/SharpLinkClient.cs:12-13`
- 问题：高频 `TryAdd/TryRemove/Clear` 的并发哈希开销高于“单连接内局部状态”真实需求。
- 优化：
  - 改为分片数组/环形槽位（与 `requestId` 映射）或 `Dictionary + lock`（单连接内冲突可控）。

3. 流式接收每次创建 `Channel`
- 位置：`src/SharpLink.Client/SharpLinkClient.cs:393`, `:418`
- 问题：每流调用固定分配。
- 优化：
  - 提供对象池化的 stream dispatcher + writer。
  - 对单生产者单消费者场景可用轻量队列替代 `Channel`。

### P1

4. `DispatchRpc` 错误路径重复 UTF8 解码
- 位置：`src/SharpLink.Client/SharpLinkClient.cs:186-193`
- 问题：`Encoding.UTF8.GetString(payload)` 可能重复执行。
- 优化：错误消息只解码一次并复用。

5. flags 判定位仍大量使用 `HasFlag`
- 位置：`src/SharpLink.Client/SharpLinkClient.cs:182`
- 问题：位标志判断建议统一为位运算，减少通用 API 额外成本。
- 优化：`(flags & PacketFlags.IsError) != 0`。

## 2.2 Server（`src/SharpLink.Server`）

### P0

1. 每请求异步状态机/调度成本
- 位置：`src/SharpLink.Server/SharpLinkServer.cs:190-263`
- 问题：`DispatchRpcAsync` 全异步，快路径没有同步完成优化。
- 优化：
  - 将分发改为 `ValueTask` 快路径（可同步完成时不分配 Task）。
  - 对 `oneway + 无流` 的简单场景走专用同步分支。

### P1

3. session 层任务创建策略
- 位置：`src/SharpLink.Server/SharpLinkServer.cs:23`, `:35`
- 问题：`Task.Run` 用于心跳循环与会话处理；连接数增大时调度开销上升。
- 优化：
  - 使用长期后台循环 + work queue 模式，减少每连接任务风暴。

4. 握手路径字符串处理
- 位置：`src/SharpLink.Server/SharpLinkServer.cs:77`, `:92`
- 问题：握手 payload UTF8 解码与固定字符串发送在高频短连场景会放大。
- 优化：
  - 协议化握手为固定字节 token，避免字符串编码解码。

## 2.3 Runtime（`src/SharpLink.Runtime`）

### P0

1. `StreamManager` 全路径 `ConcurrentDictionary`
- 位置：`src/SharpLink.Runtime/StreamManager.cs`
- 问题：每 chunk 都构造 key 并并发哈希查询。
- 优化：
  - request 级别先定位，再到 streamId 的小表。
  - 对 `streamId = 0` 建立专门快路径。


### P1

3. `PacketHelper` 头部每包 `CopyTo(stackalloc)`
- 位置：`src/SharpLink.Runtime/PacketHelper.cs:12-13`
- 问题：每包固定拷贝 15 字节，PPS 极高时可见。
- 优化：改 `SequenceReader<byte>` 直接读头。

4. `RpcSession` 发送通道/flush 策略
- 位置：`src/SharpLink.Runtime/RpcSession.cs`
- 问题：`Channel` 节点分配 + 当前阈值策略（4KB/1ms）在不同负载下可能不优。
- 优化：
  - 压测驱动调参（4KB, 8KB, 16KB 与 0.2/0.5/1ms 窗口）。
  - 提供低延迟模式与高吞吐模式配置。

5. `WriteUtf8String` 的 `GetMaxByteCount` 可能过度申请
- 位置：`src/SharpLink.Runtime/ArrayBufferWriterExtensions.cs:57-60`
- 问题：最坏估算可能导致更大 span 请求。
- 优化：短字符串可 stackalloc + TryEncode，长字符串走当前路径。

## 2.4 Generator（`src/SharpLink.Generator`）

### P0

1. 流返回生成代码中 `Task.Run`
- 位置：`src/SharpLink.Generator/RpcGenerator.cs:644`
- 问题：每次流调用都额外任务调度。
- 优化：
  - 改为直接异步泵送，必要时复用统一发送 worker。

2. blittable 跨段回退 `new byte[]`
- 位置：`src/SharpLink.Generator/RpcGenerator.cs:611-613`
- 问题：触发回退时分配临时数组。
- 优化：
  - 小尺寸 `stackalloc`，大尺寸 `ArrayPool<byte>.Shared`。

### P1

3. 流参数默认 `Channel.CreateUnbounded`
- 位置：`src/SharpLink.Generator/RpcGenerator.cs:585`
- 问题：每个流参数固定分配通道。
- 优化：生成“1/2/3 个流参数”专用代码，减少通用容器。

## 2.5 Transport

1. `Socket.Connected` 不是可靠实时连接状态
- 位置：`src/SharpLink.Runtime/SocketTransport.cs:52`
- 问题：可能导致连接状态判断延迟，带来无效路径开销。
- 优化：以读写异常与会话生命周期为主，不依赖 `Connected` 热路径判断。

2. NamedPipe 创建 reader/writer 重复
- 位置：`src/SharpLink.Runtime/NamedPipeTransport.cs:11-13`, `:26`
- 问题：属性与返回路径都有创建点，建议统一单一实例。
- 优化：只使用缓存实例，避免潜在重复包装。

## 3. `try/catch` 全量审计（src 全部位点）

以下为当前 `src/` 中所有 `catch`，并给出“保留/改造/移除”建议：

1. `src/SharpLink.Client/RpcRequestOperation.cs:65`
- 现状：`SetResult` 捕获反序列化异常并 `SetException`。
- 结论：保留（必要）。

2. `src/SharpLink.Client/SharpLinkClient.cs:460`
- 现状：`StartServerStreamRequestAsync` catch 全异常后完成流错误。
- 结论：改造。拆分 `OperationCanceledException` 与其他异常，避免把取消当错误热路径。

3. `src/SharpLink.Client/SharpLinkClient.cs:504`
- 现状：可取消流启动同上。
- 结论：改造（同上）。

4. `src/SharpLink.Client/SharpLinkClient.cs:614`
- 现状：发送客户端流失败后发送 `StreamError` 再抛出。
- 结论：保留，但可在取消场景走专门分支减少异常消息序列化。

5. `src/SharpLink.Client/SharpLinkClient.cs:627`
- 现状：`RunStreamSenderAsync` 捕获全部异常并转发到请求失败。
- 结论：保留；建议先判断是否已完成/已取消，避免重复失败投递。

6. `src/SharpLink.Server/SharpLinkServer.cs:248`
- 现状：捕获 `OperationCanceledException` 并回包 `Request canceled.`。
- 结论：保留。

7. `src/SharpLink.Server/SharpLinkServer.cs:253`
- 现状：捕获全异常并回包错误文本。
- 结论：改造。建议只包裹“用户服务调用区间”，框架自身路径尽量不走广义 catch。

8. `src/SharpLink.Runtime/RpcSession.cs:87`
- 现状：发送循环捕获取消异常。
- 结论：保留。

9. `src/SharpLink.Runtime/RpcSession.cs:105`
- 现状：`Dispose` 捕获 `ObjectDisposedException`。
- 结论：可移除（非必要 try/catch）。建议使用 `_disposed` 状态提前返回，避免异常驱动控制流。

10. `src/SharpLink.Hosting/SharpLinkServerHostedService.cs:33`
- 现状：`StopAsync` 捕获取消异常并吞掉。
- 结论：可改造为条件 await + token 判断，减少异常路径参与。

11. `src/SharpLink.Generator/RpcGenerator.cs:654`（生成代码）
- 现状：流返回生成代码 catch 全异常并发送 `StreamError`。
- 结论：保留但改造。拆分取消与业务异常，减少异常字符串构造与噪声。

## 4. 立刻可做的落地顺序（ROI）

### 第 1 批（P0，先做）

1. 客户端超时开销降级
- 默认不全局启用请求超时。
- 仅对指定接口/方法启用。
- 去掉不必要 linked CTS 路径。

2. Server 分发快路径
- `DispatchRpcAsync` 重构为可同步完成的 `ValueTask`。
- 简单 unary/oneway 直接快路径。

3. Generator 去 `Task.Run`
- 流返回从“每调用 Task.Run”改为无额外调度泵送。

4. StreamManager 热点结构优化
- 拆分 request 级索引，减少 `ConcurrentDictionary` 热键开销。

### 第 2 批（P1）

1. `TypedStreamDispatcher`/`DispatchChunkAsync` 去 async 状态机。
2. blittable 跨段回退改 `stackalloc + ArrayPool`。
3. Packet 头解析改 `SequenceReader` 直接读。
4. 优化 `HasFlag`、重复 UTF8 解码、错误消息路径。

### 第 3 批（P2）

1. RpcSession 发送队列结构替换评估（保守可先调参）。
2. 协议层错误码化，减少字符串负载。

## 5. 验证要求（防止“优化后回退”）

每批优化都必须跑以下基线：

1. `test/SharpLink.Benchmarks`（Unary + Streaming）
- 指标：`Mean`, `P95/P99`, `Allocated B/op`。

2. `test/SharpLink.LoadTest`（local + client/server）
- 指标：`qps`, `p50/p95/p99`, `fail rate`。

3. 回归门禁建议
- Unary 小包：QPS 不得低于基线 -3%。
- Streaming：吞吐不得低于基线 -5%。
- 分配：B/op 不得高于基线 +5%。

## 6. 结论

当前版本的性能回退，不是单点问题，而是“超时能力引入后的每请求对象分配 + 既有流式/分发开销”叠加造成。

最有效的修复路径是：
1. 先把超时逻辑从“全局每请求成本”改成“按需成本”；
2. 再消除服务端分发和生成器流路径中的任务/状态机固定开销；
3. 最后处理字典、编码和协议细节优化。

## 7. 超时增量开销完成度（更新）

针对以下三项：

1. `CreateRequestTimeoutCts` 在默认超时开启后每次请求创建 `CancellationTokenSource`
2. `InvokeCancellableCoreAsync` 在“用户取消 + 请求超时”并存时创建 linked CTS
3. `RegisterCancel`/`RegisterStreamCancel` 每次注册回调

当前完成度结论：**已完成（默认/显式超时路径的每请求对象创建增量开销已消除）**。

已完成（`[x]`）：

1. 已移除 linked CTS 路径  
当前实现未再使用 `CreateLinkedTokenSource`，改为分别注册用户 token 和超时 token，并通过本地去重门闩避免重复取消处理。

2. 回调闭包已去除  
改为 `UnsafeRegister(static callback, state)` 静态回调。

3. 回调 state 已池化  
`RequestCancelState` / `StreamCancelState` 已引入对象池，降低每次注册的 state 分配压力，并覆盖了“回调触发归还 + 注册释放归还”两条路径。

已完成（`[x]`，以下原“未完成”项现已落地）：

1. 默认/显式超时不再每请求创建 `CancellationTokenSource`  
超时由连接级共享调度器统一管理（时间队列 + 单 Timer）。

2. 不再使用 linked CTS  
“用户取消 + 超时”并存场景改为“用户 token 回调 + 超时调度回调”双源并行，取消去重门闩保持幂等。

3. 回调 state 池化完成  
取消/超时回调 state 均已池化，并覆盖“触发归还 + 未触发但注册释放归还”。

保留项（设计约束，非未完成项）：

1. 用户取消语义仍需要 token 回调注册  
仅在可取消调用（`CancellationToken.CanBeCanceled == true`）时发生，这是功能性成本，不再属于“默认超时增量成本”。

后续建议（进阶优化，非完成门槛）：

1. 将当前时间队列进一步替换为时间轮（大量短超时场景可再降锁竞争）；
2. 对极高 QPS 场景增加 timeout bucket 分片，减少单队列争用；
3. 仅在实际可能超时的方法上生成 timeout 路径代码（继续收窄热路径）。

## 8. 超时机制优化最终状态（已完成）

截至当前版本，`SharpLink.Client` 的“默认/显式超时带来的每请求增量对象开销”已完成治理，结论如下：

1. 已完成：移除每请求 `CancellationTokenSource` 创建  
- 默认超时与显式超时都不再走 `CreateRequestTimeoutCts`。  
- 超时改为连接级共享调度器统一管理。

2. 已完成：移除 linked CTS 路径  
- “用户取消 + 超时”并存时不再创建 linked token。  
- 改为双源触发（用户 token 回调 + 超时调度回调）并通过 requestId 去重。

3. 已完成：取消/超时 state 池化  
- `RegisterCancel`/`RegisterStreamCancel` 的 state 使用对象池复用。  
- 超时调度回调 state 同样池化，覆盖“触发归还 + Dispose 归还”两条路径。

4. 已完成：超时调度器分片化  
- 调度器从单队列升级为分片队列（多 `PriorityQueue` + 多 `Timer`）。  
- 降低高 QPS 下单锁争用和定时器热点。

5. 保留项（非缺陷，属功能成本）  
- 当调用方传入可取消 `CancellationToken` 时，仍需注册取消回调。  
- 该成本用于保留取消语义，不属于“默认超时引入的额外对象分配”。
