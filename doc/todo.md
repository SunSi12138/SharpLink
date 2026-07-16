# SharpLink 实施顺序

本文档记录当前已经确认的版本映射与执行顺序；详细协议与性能数据分别见 `protocol-v2.md` 和 `performance.md`。

## 0.4.0 checkpoint

当前 `0.4.0` 合并原路线图的 0.4 与 0.5，包含：

- Parser、Codec、资源边界、生命周期与 CI/Package 安全基线。
- 实例级 Runtime Context 与冻结配置。
- Client Factory、Server Listener、独立 Transport Connection。
- Protocol v2、字节有界单写者 SendPump。
- Client/Server 原子状态机、自动重连、GoAway 与优雅停机。
- `SharpLinkCallOptions`、deadline、metadata 与结构化错误。
- Release、Unit、Generator、Integration、NativeAOT、NuGet PackageSmoke 已通过。
- c32 Unary QPS 为前一基线的 96.75%，保留原基线并在 0.5.1 优先解决。

## 0.5 调整后顺序

原路线图 0.6 整体改名为 0.5。每个编号独立提交；前一项未通过正确性与性能验收，不开始后一项。

1. `0.5.1`：重写 PendingRequestTable，把 admission、response/error/cancel/timeout/disconnect 完成仲裁与默认 deadline 登记整合进同一槽位生命周期。
2. `0.5.2`：将生成代理收敛为 Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming 五类 Invoker。
3. `0.5.3`：实现 `PooledByteBufferWriter`，统一 payload owner 与 SendPump 归还时机。
4. `0.5.4`：实现 Source Generator DTO Codec 的最小稳定子集；MemoryPack 保持显式可选插件。
5. `0.5.5`：实现 stream/connection 按字节窗口与 `WindowUpdate`。
6. `0.5.6`：实现每 Endpoint 连接池与 power-of-two choices。
7. 完成 Release、AOT、PackageSmoke、LoadTest/Benchmark 五轮门禁后，才进入 0.6。

## 后续版本映射

- 原路线图 0.7（TLS、认证、拦截器、遥测、Hosting）改为 0.6。
- 原路线图 0.8（Discovery、Load Balancing、Resilience）改为 0.7。
- 原路线图 0.9 RC 与 v1 门禁改为 0.8 RC。

## 提交规则

- 每个实施编号至少一个可独立构建、可测试、可回退的提交。
- 每个 `0.x.0` 边界增加版本 checkpoint；不把多个未验证阶段长期堆在工作区。
- 性能改动在提交说明或 `performance.md` 中保留同环境前后数据。
