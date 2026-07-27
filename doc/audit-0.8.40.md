# SharpLink 0.8.40 深度审核

English: [`en/audit-0.8.40.md`](en/audit-0.8.40.md)

以 0.8.39 commit `8fffab7` 为精确基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | 只有响应方法或只有 OneWay 方法的 generated Stub 会在空 invocation category 中抛遗留 `RpcException`，被默认映射为 `Internal`。 | 两类空 dispatcher 都返回结构化 `Unimplemented`，并删除已无生产用途的公共 `RpcException`。 |
| P2 | `SharpLinkException` 接受 `Unknown` 和未定义枚举值，但 Protocol v2 明确拒绝序列化这些 code；自定义 mapper 可因此破坏错误交付。 | 两个构造函数都只接受已定义的具体 wire code；无效 mapper 输出进入既有安全 `Internal` fallback。 |
| P2 | Server interceptor 可调用未完成的 `next`、丢弃其 `ValueTask` 后立即返回，响应缓冲区在 terminal 仍写入时被外层释放。 | 每层保存并 join 已调用但未完成的 continuation；直接转发维持 fast path，状态采用有界自节点池。 |
| P2 | Client interceptor 同样可启动 terminal 后立即返回替代结果，使逻辑调用完成时网络 attempt 仍在后台运行。 | Client pipeline join 被丢弃的未完成 continuation，同时保留合法结果转换、异常身份和单次调用约束。 |
| P2 | Generator 已记录 response nullability，却在 Proxy/Stub C# 签名中丢失 `?`，并允许 required unary/stream 或 Client 短路响应成功返回 null。 | 源码显示类型与协议身份分离；generated service、stream 与 Client short circuit 在 required 边界拒绝 null，nullable 对照继续成功。 |

预修复 Generator 的 118 个既有测试全部通过，只有新增空分类见证失败；Abstractions 定向测试保留 21 个既有通过，只有新增公共面与非法 code 两项失败；Interceptor Integration 保留 14 个既有通过，新增四项 join/nullability/mapper 见证恰好失败，并产生 generated Proxy/Stub 的 CS8613/CS8604。伪突变复核分别覆盖两类 invocation category、两个异常构造路径、Client/Server join、scalar/stream 与 required/nullable 对照。

修复后非增量 Release 为 0 warning / 0 error，Generator 119/119、Unit 486/486、Integration 250/250。descriptor flags 从基线 48 B 压缩到 40 B，并保留旧/新 Deconstruct 形状。真实 TCP interceptor harness 的最终三进程中位数为 39.845 → 40.234 微秒（+0.98%，区间重叠），分配从约 1,584 B/op 降到 1,560 B/op；无 interceptor 分配仍为约 320 B/op。

最终 120 秒共享内存 Chaos 完成 817,230 success、318,950 expected、0 unexpected、23 次重启，Client/Server Error 均为 0，最大恢复 222 ms；drain 与五项活跃指标全部归零。NativeAOT TCP 输出 `AOT_SMOKE_PASS transport=tcp`；七个 0.8.40 包完成预提交打包，fresh-cache TCP/shared-memory functional smoke 通过。本轮仍发现新改进，连续无新改进轮次保持 0/3。
