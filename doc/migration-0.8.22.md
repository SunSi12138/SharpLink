# SharpLink 0.8.22 迁移指南

English: [`en/migration-0.8.22.md`](en/migration-0.8.22.md)

0.8.22 不改变 Protocol v2 framing、generated DTO field ID、fixed wire type、payload size 或 Manifest version。合法 0.8.21 payload 可由 0.8.22 读取，0.8.22 的合法 payload 也保持旧尺寸与字段布局。

行为变化仅影响畸形输入与 DateTimeOffset padding：generated DTO 的 Boolean、Rune、decimal、DateOnly、DateTime、TimeOnly、DateTimeOffset（包括 nullable sibling）现在对非法位模式报告 `DataLoss`。DateTimeOffset writer 会把 16-byte native representation 中不承载值的 6-byte padding 规范化为零；reader 不要求旧 payload 的 padding 已经为零，因此滚动升级不会因合法旧数据失败。
