# SharpLink 0.8.41 迁移指南

English: [`en/migration-0.8.41.md`](en/migration-0.8.41.md)

0.8.41 不改变合法 Protocol v2 framing、method ID、wire type 或 payload layout。它把 0.8.40 已生成的 response nullability 继续贯穿真实网络解码与 runtime compatibility identity。

## 解码后的 nullability

non-nullable `T` 的 unary/client-streaming response、ServerStreaming/DuplexStreaming response item，以及 ClientStreaming/DuplexStreaming request item 不再接受 Codec 解码出的 null，违约输入以 `SharpLinkException(DataLoss)` 失败。需要传输 null 的契约必须显式声明 `T?` 或 `IAsyncEnumerable<T?>`；这些声明仍可正常往返 null。

`PooledAsyncStreamDispatcher<T>` 保留原有两个参数的 `Rent` 方法，既有已编译调用保持二进制兼容；新增三参数 overload 供 generated/runtime 调用传入 payload nullability。

## Contract identity

nullable response 现在参与 runtime method/service/contract fingerprint。因此，分开生成且只在 response nullability 上不同的契约会被正确识别为不兼容。方法 ID 和既有 required-response fingerprint 不变；无需变更 endpoint routing 或 payload Codec。

## Error code

`SharpLinkErrorCode.Unknown` 是未设置状态的保留值，不是合法 Protocol v2 Error code。自定义协议调用方不得写入或发送它；reader 现在把该值与未定义枚举值一样判定为 `ProtocolViolation`。所有具体已定义 code 的数值和 round-trip 保持不变。
