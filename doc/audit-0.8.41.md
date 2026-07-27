# SharpLink 0.8.41 深度审核

English: [`en/audit-0.8.41.md`](en/audit-0.8.41.md)

以 0.8.40 commit `dd431f5` 为精确基线，本批确认五项 P2 改进。

| 等级 | 实证问题 | 修复 |
|---|---|---|
| P2 | 非 interceptor 的 unary/client-streaming 响应路径接受 Codec 解码出的 null，即使 generated contract 声明 required response。 | 将 `ResponseNullable` 贯穿全部 request operation 租用、重试与完成路径；required null 以结构化 `DataLoss` 失败，nullable 对照继续返回 null。 |
| P2 | Client 的 ServerStreaming/DuplexStreaming 接收 dispatcher 会将 required null item 入队。 | Client response stream 将 generated `ResponseNullable` 传入共享 dispatcher，并在解码边界拒绝 required null。 |
| P2 | Server 的 ClientStreaming/DuplexStreaming 接收 dispatcher 不知道参数的 `PayloadNullable`，同样接受 required null item。 | generated Stub 按每个 stream 参数传递 nullability；显式 `IAsyncEnumerable<T?>` 对照保持合法。 |
| P2 | Runtime method fingerprint 不包含 response nullability，分开编译的 required 与 nullable response contract 会呈现相同身份。 | nullable response schema 参与 method/service/contract fingerprint；method ID、wire type、payload layout 与既有 required fingerprint 不变。 |
| P2 | `Unknown` 是本地未设置状态的保留值，0.8.40 已禁止用它构造服务错误，但 Protocol v2 仍可写入和接受该 wire code。 | Error writer、validator 与 reader 统一只接受具体已定义 code；拒绝写入时保持目标 writer 未修改。 |

修复前 Generator 的 119 个既有测试全部通过，只有新增的跨独立编译 response-fingerprint 见证失败。Unit 的 486 个既有测试全部通过，恰好四个新增见证失败：scalar required null、Client response-stream null、Server request-stream null、reserved `Unknown` 双向协议边界。required/nullable、writer/raw-reader 与具体 code round-trip 对照覆盖了只修一侧、只传一条调用链或误伤合法值的伪突变。

修复后非增量 Release 为 0 warning / 0 error，Generator 120/120、Unit 490/490、Integration 250/250。真实 TCP 五进程中位数普通路径为 38.694 → 38.832 微秒（+0.36%），一层 Client+Server interceptor 为 39.911 → 40.302 微秒（+0.98%），区间重叠且分配分别维持约 320 B/op 与 1,560 B/op。required-reference stream dispatcher 三进程中位数为 13.860 → 13.860 ns/op，分配均为 1.333 B/op。

最终 120 秒共享内存 Chaos 完成 815,964 success、316,929 expected、0 unexpected、23 次重启，Client/Server Error 均为 0，最大恢复 221 ms；drain 与五项活跃指标全部归零。NativeAOT TCP 输出 `AOT_SMOKE_PASS transport=tcp`；七个 0.8.41 包完成预提交打包，fresh-cache TCP/shared-memory functional smoke 通过。本轮仍发现新改进，连续无新改进轮次保持 0/3。
