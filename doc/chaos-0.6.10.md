# SharpLink 0.6.10 chaos 与长稳门禁

## 本地 120 秒 Release Chaos

- commit：`02d468a3324e2d33d5d07f4bb9b0f22a7ba359a7`
- Runtime：.NET 10.0.2，macOS arm64，`DOTNET_PROCESSOR_COUNT=2`，concurrency 32。
- 成功调用：2,632,568。
- 预期故障注入结果：905,428。
- 非预期失败：0。
- 滚动重启：11 次。
- 最慢稳定恢复：331ms。
- 最终 gauge：connections、active calls、pending requests、streams、send queue bytes 全部为 0。

故障阶段继续使用真实滚动 listener 重启、连接失败、调用取消/deadline、Unary 与 Streaming 混合负载，以及连续五次成功探针作为恢复判定。迟到 Response/Cancel/deadline 由单一终态吸收，不得形成 tombstone、重复完成或关闭健康连接。

该轮同时验证 Release Gate 暴露的恢复和终止缺口：机会式 pool expansion 在 listener 停止窗口失败后会把低于最小容量的池交给持久 reconnect worker；本地取消会等待已经取得的 stream dispatch 归还最后 credit，再发送 Cancel；StreamManager 的终止 drain 与迟到注册只能由一方完成 dispatcher 并扣减 active 指标；Request accept 后发生的 Server drain 返回 `Unavailable`，不伪装成容量耗尽。

Chaos worker 对同步完成的预期 fail-fast 错误使用 1ms 有界退避。未退避的 2 核实验在约 49 秒内产生超过 600 万次同步异常，能够饿死要被测试的 reconnect timer，属于负载发生器自干扰而非有意义的恢复压力；恢复仍由独立的连续五次成功探针和 30 秒硬预算判定。短于六小时的进程只记录绝对 retained bytes，不使用启动/JIT/有界池预热百分比判定内存门禁。

## 24 小时发布长稳

最终 release commit 使用：

```bash
SHARPLINK_SOAK_DURATION_SECONDS=86400 \
SHARPLINK_SOAK_CONCURRENCY=32 \
SHARPLINK_SOAK_RESTART_SECONDS=60 \
eng/run-release-soak.sh
```

接受标准：

- 0 unexpected failure、crash、deadlock 或未观察后台异常。
- 每次故障后 pending、active calls、streams、credit waiter、send queue 和 framework task 在预算内归零。
- 最后六小时 retained memory 增长不超过 5%。
- 已开始的故障代次必须完成稳定五探针恢复，不能因总时长结束而跳过。

24 小时结果与最终 commit SHA 必须保存在 `artifacts/chaos/release-24h.json`。完成前不得创建 `v0.6.10` tag。
