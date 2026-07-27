# SharpLink 0.8.28 迁移指南

English: [`en/migration-0.8.28.md`](en/migration-0.8.28.md)

0.8.28 不改变 Protocol v2 framing、合法错误 payload 或生成代码。升级只会让此前不可执行或不可互读的配置更早失败。

- `SocketTransportOptions.KeepAliveTime` 与 `KeepAliveInterval` 必须为正且不超过 2,147,483,647 秒。
- TokenBucket `ReplenishmentPeriod`、FixedWindow/SlidingWindow `Window` 必须为正且不超过 2,147,483,647 ms。
- SlidingWindow 的 `SegmentsPerWindow` 仍至少为 2，并且不能超过 `Window.Ticks`。
- NamedPipe factory/listener 只接受各自支持的 `PipeOptions` bit；client 不接受 server-only `FirstPipeInstance`，listener 的 `PipeTransmissionMode` 必须是已定义值。平台本身不支持的合法模式仍按 .NET 平台约束处理。
- 直接调用 `ProtocolV2PayloadCodec.WriteError` 时，`code` 必须是已定义 `SharpLinkErrorCode`；非法值在 writer 保持未修改时抛出 `ArgumentOutOfRangeException`。

默认配置全部位于这些范围内，无需迁移。若应用把 `TimeSpan.MaxValue` 当作“永不刷新”的 rate window，请改为显式关闭相应规则；超大有限周期不是可靠的禁用机制。
