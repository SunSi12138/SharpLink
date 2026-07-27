# SharpLink 0.8.23 迁移指南

English: [`en/migration-0.8.23.md`](en/migration-0.8.23.md)

0.8.23 不改变 Protocol v2 framing、collection count 或 element layout。合法 0.8.22 collection payload 继续可读；Boolean、Rune、decimal、DateOnly、DateTime、TimeOnly、DateTimeOffset collection 中此前被接受的非法位模式现在报告 `DataLoss`。

DateTimeOffset collection writer 会把每个 16-byte native element 中不承载值的 6-byte padding 清零，reader 不要求旧 payload 的 padding 已为零。Shared-memory peer 在 server response 完成前关闭连接时，Client Connect 现在抛出 `SharpLinkException(Unavailable)`，原始 `EndOfStreamException`/`IOException` 保留为 inner exception；caller cancellation 语义不变。
