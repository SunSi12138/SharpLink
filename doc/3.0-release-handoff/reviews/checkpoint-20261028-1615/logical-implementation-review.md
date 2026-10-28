> 技术正文摘录：省略原报告末尾的内部保存安排，技术审查结论与正文未改。完整原报告 SHA-256：`d81976847963e720c746f4dc44b21ef31c5d0587f1ed15f9d20dddf2b2e9f587`。

# 独立实现审查报告

## 结论

**PASS — 精确 HEAD `d17e1ac691325b8948ca8663ba2d2b836c3ac2a0`。**

未发现阻断这个最小 logical-forwarder 实现继续进行独立机制验证的产品问题。PASS 限于**源码、设计落实及已保留的针对性正确性证据**，不代表全量测试、发布验收或性能收益已经成立。

本轮仅进行文件读取、轻量离线核验与静态分析；**没有运行 CLR、编译、测试、性能负载或新增探针**。进入静默窗口后已停止本地操作。

## 1. 精确身份与证据核验

已独立确认：

- candidate 工作树干净，HEAD 为上述 `d17e1ac…`。
- 基于 `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`，没有叠加 607 手写枚举器。
- 产品仅修改：
  - `SharpLinkClient.Invokers.cs`
  - `SharpLinkClient.Interceptors.cs`
- candidate、baseline、diagnostic overlay 三个工作树的 HEAD/status 与记录一致。
- `identities.json` 中 **12 个文件的 SHA256、长度全部匹配**。
- 三个工作树的实际测试运行目录 `SharpLink.Client.dll`，均与其记录的产品构建 DLL 字节一致。
- baseline 与 candidate 两个新增测试文件逐字节相同。
- `log-sha256.json` 的 **21/21 日志及诊断文件**哈希、长度匹配。
- 产品 patch、overlay patch 均与 Git 实际差异一致。
- baseline/candidate capture graph 的 **17 个有序字段声明完全相同**。

candidate Client DLL：
`6ce3458f1b12f4c98c2bdb4b2b6962921268bf7388fbbab1d501879dcacf114d`

本轮未重新启动 runtime 验证。保留记录显示 SDK 10.0.102、私有 runtime-only Core/AspNet 10.0.14；该安装此前的官方 archive 与已安装文件核验不被本轮静态审查冒充为新的运行时检查。

## 2. 实现符合已通过设计

### 真正删除的层

`CompletionOwnedLogicalInvocationAsyncEnumerable<T>.MoveNextAsync` 直接返回内部操作，不再：

- 调用逻辑转发 async helper；
- 创建替代 promise；
- 调用 `AsTask`；
- 二次消费结果；
- 添加每项所有权查询或计数。

因此它具备删除实际挂起时逻辑转发 async activation/continuation 的结构基础。**具体分配、调度与端到端效果仍未测量。**

### 原始迭代器语义保持

通过离线提取和去缩进比较，确认原 compiler iterator 的**整个方法体完全保留**，只增加外层 `try/finally`。

原有：

- 首次异常筛选；
- Current 读取与空值校验；
- deadline/cancellation 仲裁；
- 普通异步清理；
- physical ownership transfer；
- disposal 异常优先级；

没有因抽取 helper 或移动 catch/filter 而改变。

这比仅观察测试通过更直接地限定了改动面。

## 3. once、detach 与生命周期

`Complete()` 的顺序正确：

1. `Interlocked.Exchange` 选择一次完成者；
2. expected-owner compare/exchange 清除新增 owner 连接；
3. 原逻辑调用计数递减一次。

通知 helper 是同步方法，owner 仅作为同步局部引用使用。结合源码、实际字段图及终态字段断言，没有发现新增 owner 被另行 hoist 到 compiler iterator。

覆盖了两条必要路径：

- iterator 终态 `finally`；
- 未启动 Dispose、未取得 enumerator 等 wrapper-only fallback。

重复通知不会重复递减。成功返回 true 的 item 不通知完成，也不 detach。

这些操作只清除新增逻辑通知连接，**没有释放 CTS、动态模块 lease 或尚未完成的 physical owner**。前次设计审查提出的 retention 阻断已经落实解决。

## 4. 取消、异常、上下文与关闭

### 取消和异常

保留的公共 wrapper 测试覆盖了：

- MoveNext/Current 同步抛 OCE，用户 finally 随后取消；
- pending cleanup 期间仍保持逻辑计数；
- disposal failure 胜出的异常身份；
- pending invocation 成功/失败；
- unobserved false/fault 仍能完成记账。

内层测试进一步覆盖 fake-clock deadline filter、取消状态与身份、重复 token、pending disposal 及重入拒绝。109 项相邻 ownership 测试包含动态 ServerStreaming/Duplex、取消链接、后代 physical work 和模块 drain。

没有把 running-Current 的历史编译器表现升级成公开契约。

### EC / SynchronizationContext

保留 compiler builder 和原 await 策略；新完成回调没有用户代码或额外异步工作。已有测试验证调用者上下文隔离、producer cleanup 顺序，并有动态 pending-disposal 的不捕获上下文检查。

这不等于证明所有自定义 scheduler、所有 EC 通知次数都相同；**这种偶然细节相等也不是本次正确性标准**。

### Close/publication overlay

overlay `60c02470fdad4385d863b5146dc417d7818aea16` 与产品 DLL 明确分离。

两项诊断确实将 gate 放在：

- source cleanup 已完成；
- count 为 0；
- owner 已 detach；
- public move 仍未完成；

随后调用真实 `client.StopAsync`，释放 gate，要求 false 或原异常继续正确发布。

这支持所定位的关闭边界，但范围是**本地短路 source 的两个诊断场景**，不是所有 TCP、cluster retirement 或 physical-transfer 竞争的穷尽证明。后者不能用这两个 overlay 结果替代。

## 5. 测试证据及误覆盖处理

| 最终保留结果 | 通过 | 失败/跳过 |
|---|---:|---:|
| candidate dedicated logical tests | 22 | 0/0 |
| baseline 同源 dedicated tests | 22 | 0/0 |
| candidate inner-iterator tests | 17 | 0/0 |
| baseline 同源 inner tests | 17 | 0/0 |
| candidate adjacent ownership tests | 109 | 0/0 |
| 独立 overlay publication tests | 2 | 0/0 |

这些是**作者已执行、我核验的保留结果**，不是本轮重新运行的结果。

早期 14/20 项通过不能算优化路径验收：保留 graph 明确显示它们进入 Telemetry fallback。最终版本增加实际 wrapper 身份断言，并显式单测 telemetry fallback；没有通过修改产品来关闭 telemetry。该处理可信，没有把早期误覆盖混入最终证明。

当前外部-root 测试、终态 owner 字段断言及源码/字段图共同支持新增连接已解除；不应扩大为“任何合法外部引用存在时所有关联对象都必须可回收”。

## 6. PASS 的明确边界

本次没有完成或宣称：

- 全量 unit/integration、协议或所有部署配置验收；
- 新实现的真实 TCP 性能测量；
- 全部 native/inlining closure；
- 分配或 continuation 数量收益归因；
- 全部 public-wrapper 非法并发使用组合的穷尽验证；
- NativeAOT、发布、合并或上线批准。

后续应使用未插入 diagnostic hook 的产品 DLL，明确报告 telemetry 配置，并验证真实路径与全部测试单元，不能仅凭方法变小或某个分配斜率推定收益。调用者 `AsTask` 等桥接可能抵消部分收益。

**607 的独立性能测量与本审查分离。代码增大本身不证明性能无收益；本次也没有用静态大小否决任何候选。**
