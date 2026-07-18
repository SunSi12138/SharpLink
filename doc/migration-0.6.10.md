# SharpLink 0.6.9 → 0.6.10 迁移说明

0.6.10 没有破坏性公共 API 变更。Protocol v2 minor 从 1 升到 2，但 `CancellationReason` 是可选 capability：0.6.10 与 0.6.9 连接时自动退回空载荷 Cancel，不需要同时升级 Client 和 Server。

需要处理的编译期诊断：

- Streaming 契约必须在参数末尾声明 `CancellationToken`，否则产生 `SHARPLINK014` Error。
- 确认业务枚举本身不可协作取消时，可以在方法上声明 `[NonCancellable]`；框架 stream pump、dispatcher、窗口等待和连接资源仍然可取消。
- `[NonCancellable]` 与 `CancellationToken` 不能同时出现，否则产生 `SHARPLINK015` Error。
- Unary 规则不变：缺少 Token 且没有 `[NonCancellable]` 时仍为 `SHARPLINK004` Warning。

运行时语义保持稳定：

- 用户 Token 取消在客户端得到 `OperationCanceledException`。
- deadline 到期固定得到 `SharpLinkException(DeadlineExceeded)`。
- 没有业务 Token 的调用超时后，服务端不会强制终止用户 Task；调用进入 abandoned，迟到成功或异常响应被丢弃，Task 和 DI scope 继续被观察到真实结束。
- 业务方法拥有 Token 时，服务端在取消 Token 前先发布稳定原因。应用不应根据取消回调发生的线程或绝对 UTC wall clock 推断原因。
- stream 提前退出现在明确映射为 `ConsumerAbandoned`，并立即释放框架额度；它不表示服务业务已成功。

可观测性新增两个兼容指标：

- `sharplink.calls.abandoned` 增加 `rpc.sharplink.termination_reason`。
- `sharplink.responses.late_dropped` 逐次统计迟到响应。对应 Warning 每物理连接最多五秒一次。

如果监控系统对 metric tag 使用 allow-list，请在升级前加入 termination reason。若契约因 `SHARPLINK014` 失败，优先添加 Token；只有明确接受业务不可取消语义时才添加 `[NonCancellable]`。
