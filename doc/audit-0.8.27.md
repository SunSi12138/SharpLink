# SharpLink 0.8.27 深度审核

English: [`en/audit-0.8.27.md`](en/audit-0.8.27.md)

以 0.8.26 commit `1d8325e` 为基线，本批确认五项 P2 改进：payload-bearing response 在 payload 为空时绕过 Codec 并静默产生 `default(T)`；consumer enumeration token 覆盖 response stream 的 call/lease token；writer Return 与 pool Dispose 竞态可把 ArrayPool-backed writer 放入已脱离的 queue；Hosted Server 的 run loop 在启动后正常意外退出不会停止 Host；匿名管道首次建连失败后重置 one-shot gate，允许复用可能已消费或关闭的继承句柄。

完整修复前 Unit 运行共 454 项，原有 449 项全部通过、新增五项恰好失败。证据分别为静默 `default(int)`、call cancellation 被屏蔽 250 ms、15 ms 竞态探针留下一个 detached writer、Server 退出后 500 ms 内未请求 Host stop，以及第二次匿名管道尝试泄漏为 `UnauthorizedAccessException` 而非 one-shot 拒绝。

pending operation 现在显式保存 response 是否携带业务 payload：需要 payload 时即使为空也调用 Codec，不需要 payload 时只接受空 acknowledgement。stream dispatcher 保留一个主 token，并仅在两个可取消 token 不同时增加第二个注册，普通单 token 路径不增加对象分配。writer Return 在 enqueue 后复核 pool 所有权，Dispose 竞态下由任一方排空同一个 detached queue。Hosted Service 以自己的 stop 标志区分正常关闭与意外成功退出。匿名管道 gate 在首次 attempt 开始后永久关闭。

补强后 Unit 454/454；精确最终树的非增量 Release 构建为 0 warning / 0 error，Generator 101/101、Integration 237/237、七包与全新缓存 package smoke 全部通过。15-sample A/B 的三条相关热路径均保持原分配并通过延迟门禁。详见 [`performance-0.8.27.md`](performance-0.8.27.md) 与 [`migration-0.8.27.md`](migration-0.8.27.md)。
