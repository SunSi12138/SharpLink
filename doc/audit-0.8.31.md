# SharpLink 0.8.31 深度审核

English: [`en/audit-0.8.31.md`](en/audit-0.8.31.md)

以 0.8.30 commit `6ecdac9` 为基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | Socket factory 只复制三个内置 `EndPoint`；其他可变端点按引用保留，构建后修改源对象即可改变连接目标。 | 自定义端点必须通过 `Create(Serialize())` 产生独立快照；不能履约或返回自身时在配置阶段拒绝。 |
| P2 | UDS listener 只记路径；其 socket node 被 unlink 并由调用方文件替换后，释放 listener 仍会删除替换物。 | 通过 .NET `System.Native` 稳定 `lstat` ABI 记录 socket 类型/device/inode；释放期间临时保护不同身份的 replacement，只删除自己绑定的 node。 |
| P2 | 公共 raw frame token 只是 offset，可从 writer A 取 token 后静默回填 writer B；加入 writer identity 又会拖慢每帧热路径。 | 保持原始无分配方法体不变，把框架重复且易误用的 raw writer/token 收回内部；受支持路径仍为生成代码和内部 packet writer。 |
| P2 | BCL 要求父进程在子进程继承 anonymous-pipe handles 后释放本地 client-handle 副本；offer 没有交接完成入口，record 诊断还打印 handle。 | offer 实现幂等 `CompleteHandleTransfer`/`Dispose`，同时关闭两个父进程副本、聚合双重失败，并固定脱敏 `ToString()`。 |
| P2 | 两个旧 Generated Registry 用静态字典强引用 Type/delegate，多个零消费者接口/集合及实现 helper 仍占公共 API。 | 删除无生成器/运行时/文档消费者的 registry、`ISerializer`、`IServiceRegister`、`StripedLongSet`；其余 packet/buffer/striped helper 改为 internal。 |

官方证据：[`DisposeLocalCopyOfClientHandle`](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.anonymouspipeserverstream.disposelocalcopyofclienthandle?view=net-9.0) 明确要求父进程在交接后关闭本地副本，否则 Server 无法收到 client disposal；.NET runtime 的 [`Interop.Stat`](https://github.com/dotnet/runtime/blob/main/src/libraries/Common/src/Interop/Unix/System.Native/Interop.Stat.cs) 与 [`pal_io.h`](https://github.com/dotnet/runtime/blob/main/src/native/libs/System.Native/pal_io.h) 给出跨 Unix `FileStatus` 布局、socket 类型和承诺稳定的 native ABI。

完整预修复 Unit 为 473 项：原有 468 项全过，且仅五个新 probe 失败。修复后删除三个仅覆盖废弃 registry 的测试，最终 Unit 为 470/470。断言与伪变异复核能分别击杀：回退到源 endpoint、绕过 path identity/保护、重新公开 raw writer/token、移除任一 handle 的完成或脱敏、保留任一废弃/实现类型为 public。

最终非增量 Release 构建为 0 warning / 0 error，Generator 102/102、Unit 470/470、Integration 237/237、七包与全新缓存 smoke 全部通过。热路径证据见 [`performance-0.8.31.md`](performance-0.8.31.md)，破坏性 API 迁移见 [`migration-0.8.31.md`](migration-0.8.31.md)。连续无新改进轮次仍为 0/3。
