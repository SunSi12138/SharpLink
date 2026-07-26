# SharpLink 0.8.20 迁移指南

English: [`en/migration-0.8.20.md`](en/migration-0.8.20.md)

0.8.20 不改变 Protocol v2 framing 或 generated Manifest。`SharpLinkProtocolOptions.HandshakeTimeout`、`SharedMemoryTransportOptions.HandshakeTimeout` 和 Client/Server TLS handshake timeout 现在必须为正值且不超过 2,147,483,647 ms（约 24.8 天）；更大的值会在配置期抛出 `ArgumentOutOfRangeException`。需要无限等待时，应由调用方使用可取消生命周期管理，而不是伪造多年握手超时。

远期 RPC absolute deadline 仍然受支持。断线 readiness、满 pending table 和 Server graceful drain 会按便携 timer 范围分片，调用方取消、owner 完成与真实 deadline 仍按原语义竞争。

generated DTO 的 string field 现在严格要求合法 UTF-8。旧版本会把畸形 bytes 静默替换为 U+FFFD；0.8.20 改为抛出 `SharpLinkException` 且 code 为 `DataLoss`。合法编码的 U+FFFD 仍可正常往返，因此无需修改正常文本数据。
