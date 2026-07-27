# SharpLink 0.8.21 迁移指南

English: [`en/migration-0.8.21.md`](en/migration-0.8.21.md)

0.8.21 不改变 Protocol v2 wire format 或 generated Manifest。generated DTO string 和 `SharpLinkMetadata` key/value 必须是合法 Unicode；含孤立 high/low surrogate 的本地值现在在编码前抛出 `EncoderFallbackException`，不会再以 U+FFFD 发送。合法 surrogate pair（包括 emoji）不受影响。

畸形 shared-memory mapping path 现在以 `FailedPrecondition` 在握手层拒绝，而不是被替换后落入 filesystem `PermissionDenied`。带 trailing bytes 的 null generated collection 现在报告 `DataLoss`。动态 per-call DI scope factory 抛错仍保留原异常，但不再阻塞模块卸载。
