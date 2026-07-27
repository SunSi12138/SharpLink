# SharpLink 0.8.32 深度审核

English: [`en/audit-0.8.32.md`](en/audit-0.8.32.md)

以 0.8.31 commit `818f23e` 为基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | UDS 已 bind、尚未捕获 identity 的窗口内，路径被替换且 identity 捕获失败时，旧 cleanup 会让 socket dispose 删除调用方替换物。 | identity 缺失代表无法证明所有权；cleanup 只要看到现存路径就先保护它，不再删除未知归属条目。 |
| P2 | Runtime Context 虽宣称 Build 后冻结，却在握手广告、选择、查找与 session 错误中反复读取自定义 provider 的可变 `WireProfile`。 | Build 时一次校验并冻结 profile/provider binding；运行期只消费冻结 identity，provider 实例仍负责压缩执行。 |
| P2 | provider 可绕过 `Reject` factory，通过 public primary constructor 返回未定义非零错误码；Server 把它交给严格 encoder 后握手异常终止。 | factory 拒绝未定义值，Server 信任边界同时把绕过 factory 的未定义 rejection 归一化为 `AuthenticationRejected`。 |
| P2 | 配置接受任意正 `TimeSpan`，但 `TimeSpan.MaxValue` 在请求发送前使 `DateTimeOffset.Add` 溢出。 | 普通调用与 health check 都把超出表示范围的正 timeout 饱和到 `DateTimeOffset.MaxValue`。 |
| P2 | concurrency-only admission 的同步成功路径每调用分配固定八槽 slot、retained lease 和 acquired lease 三个数组，共 568 B。 | 按实际规则数创建 slot；无 retained limiter 的单槽成功路径直接把一个 lease 移交给 `AdmissionLease`。 |

完整预修复 Unit 为 474 项：原有 470 项全部通过，仅四个新功能探针失败，admission 探针记录 568 B/call；Integration 为 238 项，原有 237 项全部通过，仅新认证探针失败。修复后断言与伪变异复核能分别击杀空 identity 删除、运行期 profile 重读、未定义认证码透传、deadline 溢出和三数组恢复。

custom compression overrun 假设被实证否决：精确容量的 leased packet writer 已在越界字节可见前抛出。首个 admission 池化方案虽降到 232 B，却回退到 93.996 ns，因此同样被否决。最终非增量 Release 构建为 0 warning / 0 error，Generator 102/102、Unit 474/474、Integration 238/238、七包与全新缓存 smoke 全部通过。性能证据见 [`performance-0.8.32.md`](performance-0.8.32.md)，行为说明见 [`migration-0.8.32.md`](migration-0.8.32.md)。连续无新改进轮次仍为 0/3。
