# SharpLink 0.6.10 chaos 与长稳门禁

## 本地 120 秒 Release Chaos

- commit：`f3b7fd3997735b9cd688e07677d94c500e0ca972`
- Runtime：.NET 10.0.2，macOS arm64，concurrency 32。
- 成功调用：2,943,131。
- 预期故障注入结果：2,933,391。
- 非预期失败：0。
- 滚动重启：10 次。
- 最慢稳定恢复：6.589 秒。
- 最终 gauge：connections、active calls、pending requests、streams、send queue bytes 全部为 0。

故障阶段继续使用真实滚动 listener 重启、连接失败、调用取消/deadline、Unary 与 Streaming 混合负载，以及连续五次成功探针作为恢复判定。迟到 Response/Cancel/deadline 由单一终态吸收，不得形成 tombstone、重复完成或关闭健康连接。

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
