# SharpLink 0.8.4 深度审核

English: [`en/audit-0.8.4.md`](en/audit-0.8.4.md)

本批以 0.8.3 commit `fb1585e` 为基线，审核 Codec 动态发布、Runtime Context 生命周期、client-stream admission 以及 multi-cluster 动态程序集协调。五项互相独立的 P2 以上问题均先通过确定性失败测试实证，再修复并完成全套验证。

| 等级 | 问题与实证 | 修复与验证 |
| --- | --- | --- |
| P1 | generated factory 或 fallback resolver 在旧快照下阻塞时，新 Manifest 可先完成发布；旧调用随后仍缓存并返回过期 wire Codec。 | factory/resolver 返回后及缓存发布后重新验证 registration identity；两条受控竞态均只能返回当前 generation。 |
| P2 | Codec resolution 仅在入口检查 disposed；阻塞 resolver 可在 Context Dispose 清空后重新填充缓存并成功返回。 | 所有潜在阻塞解析点后重新检查生命周期；受控竞态改为 `ObjectDisposedException`。 |
| P1 | pre-admission `Attach` 在请求注册路径同步等待异步 replay；有界消费端尚未启动时形成确定性死锁。 | 回放期间继续使用既有有界队列，注册立即返回；队列排空后原子发布 dispatcher，保留帧与 live 帧顺序不变。 |
| P2 | dispatcher 的消费回调与 dispatch-state 绑定在 per-request registry lock 内执行；回调重入同一 registry 会锁死。 | lock 内仅认领 entry/dispatcher；配置、回放与用户回调全部在 lock 外执行，entry lease 保护并发 remove/detach。 |
| P1 | multi-cluster child 已发布 replacement，但旧 generation cleanup 随后失败时，coordinator 因 await 抛错而仍路由旧 assembly。 | 失败路径查询 child registration inspector，并以幂等 publication 对账 coordinator；原 cleanup 异常继续抛给调用者。 |

全源性能清单扫描还覆盖 string comparison、sync-over-async、集合/LINQ 分配、静态可变集合、HTTP/JSON/Regex 构造及 sealed 候选。唯一晋级的 sync-over-async 命中即 pre-admission replay；其余命中位于生成/构建冷路径或缺少可测工程收益，未为凑版本而改动。静态 source/test pairing 仅用作导航，因为历史 benchmark baseline 源副本会放大未配对计数。

迁移见 [`migration-0.8.4.md`](migration-0.8.4.md)，性能门见 [`performance-0.8.4.md`](performance-0.8.4.md)。
