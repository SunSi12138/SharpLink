# SharpLink 1.0.0 性能与稳定性

SharpLink 1.0.0 的核心结论：主要 .NET RPC 场景吞吐领先 grpc-dotnet，在合理并发下达到 gRPC C++ 等价实现的吞吐水平，四服务端可接近 195 万 QPS，并通过了 24 小时零错误稳定性测试。

## 核心性能

| 场景 | SharpLink | grpc-dotnet | 结果 |
|---|---:|---:|---:|
| 256 B Unary，1S1C，c1 | 10,249 QPS | 8,450 QPS | SharpLink +21.3% |
| 256 B Unary，1S3C，c128 | 748,915 QPS | 473,295 QPS | SharpLink +58.2% |
| 4 KiB × 8 Duplex，1S2C，c32 | 21,379 RPC/s | 18,990 RPC/s | SharpLink +12.6% |

小请求延迟方面，1S1C Unary 的 SharpLink P50/P99/P99.9 为 `97/127/203 µs`，grpc-dotnet 为 `115/142/631 µs`。

## gRPC C++ 对照

Ubuntu 7950X 裸机上，SharpLink 与 gRPC C++ 使用相同 4 KiB × 8 Duplex 契约、独立进程、相同绑核、16 transport lanes 和 c128，五轮中位数为：

| 框架 | 吞吐 | P99 |
|---|---:|---:|
| SharpLink | 734,421 message/s | 4.085 ms |
| gRPC C++ 1.82.1 | 714,280 message/s | 2.293 ms |

SharpLink 吞吐高 `2.8%`，但 gRPC C++ 的尾延迟更低。这是最适合直接比较的 C++ A/B 数据。

云端 gRPC C++ 参考结果为：等价 Unary c128 `255,407 QPS`、等价 Duplex c32 `142,683 message/s`、官方 Async Unary worker `144,521 QPS`、官方 Callback Duplex worker `154,019 QPS`。等价 Duplex 受自写 C++ 客户端负载器限制；官方 worker 和等价业务负载器调用方式也不同，因此这些结果只作环境参考，不与其他拓扑直接计算倍率。

## 横向扩展

| 服务端规模 | QPS | 相对 1S |
|---|---:|---:|
| 1S | 572,014 | 1.00× |
| 2S | 975,253 | 1.70× |
| 4S | 1,947,159 | 3.40× |

从一个服务端扩展到四个服务端后，SharpLink 达到约 `195 万 QPS`，扩展效率为 `85.1%`。

## 稳定性与恢复

| 项目 | 结果 |
|---|---:|
| 24 小时连续混合负载 | 414,775,951 次成功，0 RPC/校验错误 |
| 2S2C 服务进程重启 | 6.467 秒恢复，0 内容错误 |
| 上海—广州基础 RTT | 平均约 29.37 ms |
| 上海—广州无节拍吞吐 | 79,350 QPS，0 错误；受 200 Mbps 公网链路限制 |

## 结论与范围

- SharpLink 在三个主要 .NET 对照点分别领先 grpc-dotnet `21.3%`、`58.2%` 和 `12.6%`。
- 在公平的 c128 Duplex A/B 中，SharpLink 吞吐达到 gRPC C++ 的 `102.8%`；gRPC C++ 仍有更好的 P99。
- 四服务端正式测试达到约 `195 万 QPS`。
- 24 小时累计超过 `4.14 亿` 次成功调用，没有 RPC 或内容校验错误。

测试产品候选为 SharpLink `1.0.0-rc7`，commit `36a80656be91822556942a2841750ba8555d2ead`。稳定版只在该候选之上更新发布元数据和文档，不改变运行时代码。以上均为特定硬件、消息尺寸和并发下的实测结果，不代表所有环境的固定上限。
