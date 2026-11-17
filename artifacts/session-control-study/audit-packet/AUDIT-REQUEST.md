# Copy-Paste Independent Audit Request

请对 SharpLink Issue #998 的实验进行独立、严格、对抗性审计，关联主设计 Issue #775。

实验 Issue：https://github.com/SunSi12138/SharpLink/issues/998

主设计 Issue：https://github.com/SunSi12138/SharpLink/issues/775

执行分支：`study/998-session-control`
https://github.com/SunSi12138/SharpLink/tree/study/998-session-control

基线 SHA：`2948de82fff77d66fce51ecec9a5783b6ef3cb62`

候选 SHA：
- 工具链隔离提交：`b499d2cfff22a424ea3448c5be73fbeba49bcb99`
- S 初始原型：`58c280f4ef02c0620d510cc1a78139f2ec5e80ce`
- S 测试矩阵：`5b8d5fd269cf7d1e48172be1e1d07e13593d74d1`
- S 最终产品源码：`525a5aed918516ac1214a91c99fbb3fbb3a746e9`
- S 最终测试/证据提交：`7d21b89a86d95f79b0c2d5397ca737bcadf668c6`
  https://github.com/SunSi12138/SharpLink/commit/7d21b89a86d95f79b0c2d5397ca737bcadf668c6

源代码与 Diff 链接：
- https://github.com/SunSi12138/SharpLink/compare/2948de82fff77d66fce51ecec9a5783b6ef3cb62...525a5aed918516ac1214a91c99fbb3fbb3a746e9
- 审计包内 `evidence/source/s-product.diff`、`s-tests.diff`、`C_NEG_Test.cs`、`RuntimeAssemblyIntegrationTests.SessionControlStudy.cs`

GitHub Actions 链接：**无**。本实验全部在本机 Windows 执行，没有 Actions run、job、attempt 或 artifact。不要把本地日志当成 CI 证据。

原始证据工件位置：
`artifacts/session-control-study/audit-packet/`
（若该目录未进入远端分支，则以同目录 ZIP 及 SHA-256 为准；ZIP 清单在 `SHA256SUMS.txt`。）

审计材料：`audit-packet/01-baseline.md` 至 `10-independent-audit-request.md`，以及 `audit-packet/evidence/` 下的 events.jsonl、result.json、stdout 日志、失败清单、源码 diff 和二进制 SHA-256。
在线目录：https://github.com/SunSi12138/SharpLink/tree/study/998-session-control/artifacts/session-control-study/audit-packet

---

请不要相信实验执行 AI 的最终结论，也不要仅仅复述其总结。请独立完成：

1. **流程审计**
   检查是否严格按照 Issue #998 的 G0–G7 阶段、门禁、预注册条件和停止规则执行，是否存在跳过、补跑、修改门槛或选择性报告。特别核查：
   - 是否真的以 `dev @ 2948de82` 为基线；
   - 是否只改了研究分支；
   - 是否在 S 未完成 F01/F02、F11 late-ack、F12–F17/F19–F24 等义务时停止性能阶段；
   - 失败 attempt 是否被保留；
   - 是否把测试自身错误误算成产品通过/失败。

2. **源码审计**
   检查 S 的真实实现是否符合其声明架构，尤其是：
   - `TryBeginDraining` 是否真的是 cut 的线性化点；
   - cut 是否确实在新 `_services` 发布之前；
   - 所有请求路径是否都使用 `PinnedContractServices`，有没有遗漏回落 live registry；
   - `RetirePinnedSessionsAsync` 的发送、失败、丢失、重排、重复语义；
   - 旧 Session 快照对 ALC 的强引用和清理；
   - 是否引入 ABA、无界状态、死锁或 per-RPC 分配。

3. **测试有效性审计**
   检查 F01–F24 的真实覆盖，确认测试能区分正确实现和缺陷实现。特别核查：
   - C-NEG 是否确实运行在 P 代码上，且其 “PASS” 只表示捕获了 hazard；
   - F05 的 cut/hook 是否在真正线性化点之后触发，是否存在未测试窗口；
   - F07 的 permit/queue 是否真实；
   - F09/F10 是否真的使用两个独立物理 Session；
   - F18/F18b 是否真的持有真实 duplex 流、旧 Session/ALC 在 held 时未提前释放、释放后可回收；Unregister/server-stream 子场景是否仍缺失；
   - S 全量 Integration 为何在 63 个 unique failure 后无输出、被强制停止；这是 hang 还是测试等待；
   - 14 行 `QueuedSelectionRetirement...` 是否真的全部执行（P 的 all-14 run 与一次只跑 1 行的 selector mistake 要区分）。

4. **证据审计**
   逐项核实 SHA、测试 UID、实际命令、退出码、工件和事件时间线。重点核查：
   - `525a5aed` 是否确为最终 S；
   - 七个必需核心 case、F18b、F04、F08、F11 是否对应 `attempt-final-4f92` / `attempt-canonical-4f92` / `attempt-final-33fb` / `attempt-final-948f` / `attempt-final-7d21`；
   - 失败 attempt 是否未被覆盖；
   - C-NEG 事件 `stale request → value=2, R1=0, R2=1` 是否真实；
   - P 的 528/482/46/0、Unit 2255/2229/26/0、Generator 295/294/1/0、Load 77/73/4/0 是否被正确引用；
   - 46 个 Integration 失败是否全部可归因于 SharedMemory/TLS/UDS/临时路径 ACL；
   - 没有 GitHub Actions 这一事实是否被隐瞒。

5. **性能审计**
   本实验**没有**运行 A/A、ABBA/BAAB、TCP/SharedMemory 性能矩阵；没有 CPU/op、B/op、P50/P99/P99.9 样本；没有正式性能结论。请核实是否存在未报告的隐藏性能数据。若没有，明确判为 `INCONCLUSIVE`；不要接受“S 更快/不退化”的任何说法。

6. **架构审计**
   判断“控制帧 + Session 快照”是否真的能在保持 #775 语义的前提下取消 BindingId，还是仅将复杂度转移到 Session 关闭、版本同步、动态注册和状态管理。重点：
   - S 是否支持同一 Session 多契约、局部 Replace、不打断无关服务；
   - 动态 `RegisterAssembly` 后现有 Session 是否还能用；
   - 控制帧丢失是否导致永久 `Unavailable`；
   - 长流是否延长旧 Session/ALC 生命周期；
   - 它是否满足 N0 的 exact RID/receipt/queue-only cut 义务。

7. **结论复核**
   提出支持与反对实验结论的证据，列出 P0/P1/P2 问题、未证明的义务及下一步最小验证方案。特别判断：
   - “S 在窄 Replace 窗口能阻止 stale request 进入 R2”是否成立；
   - “S 作为 N0 替代方案 NO-GO”是否成立；
   - “整体实验 INCONCLUSIVE”是否成立；
   - 是否有证据支持修改 #775 架构。

审计结束时请明确给出：

- 实验执行流程是否可信；
- 各候选正确性是否得到充分证明；
- 性能比较是否有效；
- 哪些结论可以接受，哪些不能接受；
- 是否有证据支持修改 #775 架构；
- 最终决定：`APPROVE_RESEARCH_CONCLUSION / REQUEST_MORE_EVIDENCE / REJECT_CONCLUSION / INCONCLUSIVE`。

在独立审计完成前，不得将实验结论当成已确认的工程决策。
