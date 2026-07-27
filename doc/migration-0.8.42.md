# SharpLink 0.8.42 迁移指南

English: [`en/migration-0.8.42.md`](en/migration-0.8.42.md)

0.8.42 不改变合法 Protocol v2 framing、method/field ID 或有效 payload bytes。主要行为变化是拒绝此前被宽松接受的非规范输入，并修正本地 writer 的错误分类。

## Codec 规范输入

非 nullable `Memory<T>` 与 `ReadOnlyMemory<T>` 不再把 `-1` collection marker 当成 empty；该输入现在以 `SharpLinkException(DataLoss)` 失败。nullable array/list 的 null 与 `ImmutableArray<T>` 的 default 表示不变。

fixed-width nullable primitive 的 null body 必须全零。SharpLink 自带 serializer 一直生成该规范形式；自定义生产者若在 null marker 后发送非零 ignored bytes，升级后会得到 `DataLoss`。present value 的布局不变。

## Writer 错误域

传给 `WriteCancelReason`、`WriteHealthResponse`、`WriteHandshakeRequest` 或 `WriteHandshakeResponse` 的无效本地 enum/limit 现在在 writer 未推进时抛 `ArgumentException` 家族异常。相同非法 bytes 来自 peer 时，reader 仍返回 `SharpLinkException(ProtocolViolation)`。

## DTO schema identity

nullable reference member 现在参与 generated runtime Codec `SchemaId`。只在 member nullability 上不同的分离构建会正确判定为不兼容；既有 non-nullable DTO schema identity、field ID 与 payload layout 不变。双方应使用同一契约版本重新生成 nullable DTO 工件。

Throughput batching 的修复没有公共 API 或 wire 变化，只消除了高并发流式负载下的进程级竞态。
