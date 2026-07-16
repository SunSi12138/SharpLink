# SharpLink Protocol v2

Protocol v2 是 SharpLink v1 的唯一线协议，不提供 Protocol v1 兼容或恢复扫描。任何 magic、长度、类型、标志或载荷结构错误都作为连接级 `ProtocolViolation` 处理并关闭连接。

## 固定帧头

所有整数使用 little-endian。固定头共 15 字节：

| Offset | 长度 | 字段 | 约束 |
|---:|---:|---|---|
| 0 | 1 | magic | 固定 `0x89` |
| 1 | 4 | payload length | 有符号 `int32`，必须在 `0..MaxFramePayloadBytes` |
| 5 | 1 | frame type | 必须是已定义的 v2 类型 |
| 6 | 1 | frame flags | 未知 bit 或类型不允许的 bit 均非法 |
| 7 | 8 | request ID | `uint64`；连接级控制帧固定为 0，成对控制请求使用非 0 correlation ID |

解析器必须先验证完整固定头与 payload length，再等待或切分 payload。半帧保留在输入缓冲；坏帧不尝试寻找下一个 magic。

## 帧类型与载荷

| 类型 | Request ID | 载荷 |
|---|---:|---|
| `HandshakeRequest` | 0 | minor、supported/required capabilities、本端 frame/window 限制、认证载荷 |
| `HandshakeResponse` | 0 | 协商后的 minor、capabilities、frame/window 限制；失败时为二进制错误 |
| `Ping` / `Pong` | 0 | 发送端 monotonic timestamp (`int64`) |
| `Request` | 非 0 | `contractId:uint64 + methodId:uint64`，随后是可选 deadline、metadata 和业务 payload |
| `Response` | 非 0 | 成功时直接为返回 payload；`Error` 时为二进制错误 |
| `Cancel` | 非 0 | 空 |
| `StreamData` | 非 0 | `streamId:uint16 + item payload` |
| `StreamComplete` | 非 0 | `streamId:uint16`；`Error` 时追加二进制错误 |
| `WindowUpdate` | 非 0 | `streamId:uint16 + credit:uint32`，credit 必须在 `1..int32.MaxValue` |
| `GoAway` | 0 | `lastAcceptedRequestId:uint64 + binary error/reason` |
| `HealthCheck` | 非 0 | 空；只在协商 health-check capability 后发送 |
| `HealthResponse` | 非 0 | 单字节 `Unhealthy/Ready/Draining` 状态 |

Stream ID 0 表示默认返回流，1–65535 表示显式流参数。Request ID 0 仅用于连接控制帧；溢出分配时必须跳过 0。

## 标志

- `Error`：载荷使用二进制错误格式。
- `Truncated`：错误消息已在 UTF-8 字符边界截断，只能与 `Error` 同时出现。
- `HasDeadline`：Request 路由前缀后包含绝对 UTC deadline（Unix milliseconds，`int64`）。
- `HasMetadata`：deadline 后包含 `varuint length + metadata bytes`；metadata payload 为 `entryCount:varuint`，随后重复 UTF-8 key/value 的 `varuint length + bytes`。
- `Compressed`：对应载荷已压缩，必须先通过能力协商。
- `Cancellable`：调用允许远端取消。
- `OneWay`：单向请求，不得同时设置 `HasReturn`。
- `HasReturn`：请求期待返回 payload。

类型未列出的标志组合一律非法。

## 握手与能力

Transport（TCP 使用 TLS 时先完成 TLS）建立后，Client 首先发送 `HandshakeRequest`。Server 返回双方能力交集和较小的 frame/window 限制。当前 capability bits：

- bit 0: metadata
- bit 1: compression
- bit 2: flow control
- bit 3: protocol health check

对端缺少任一 required capability 时，Server 返回 `Unimplemented` 错误并关闭连接。认证载荷不得超过握手/metadata 上限。

## 流量控制

协商 `flow control` 后，每个 `StreamData` 的 item payload 同时消耗所属 stream 和 connection 的发送额度。额度不足时 producer 异步等待；`WindowUpdate` 的 request ID 与 stream ID 精确定位原 stream，credit 同时补充两级窗口。

接收端只在消费者实际取得或明确丢弃 item 后归还 encoded byte credit，默认达到任一半窗口阈值后批量发送更新。大于 stream window 的单个 item 在不超过 `MaxFramePayloadBytes` 时允许从空窗口临时借用一次；借用未归还前继续发送即为 `ProtocolViolation`。任何 credit 加法溢出、重复归还或超过协商初始窗口也按连接级协议错误处理。

## 二进制错误

错误载荷格式为：

```text
errorCode:uint16 + messageLength:varuint32 + UTF8 message
```

错误消息默认上限 64 KiB；发送端在 UTF-8 字符边界截断并设置 `Truncated`。未知错误码、超限长度或长度不匹配均为 `ProtocolViolation`。服务实现未显式映射的异常只发送安全的 `Internal` 消息，不发送堆栈。

## 安全边界

- 默认 frame payload 上限 4 MiB，可配置范围 1 KiB–64 MiB。
- metadata 默认上限 16 KiB，错误消息默认上限 64 KiB。
- 所有网络长度在 `Slice`、复制或分配前验证。
- `HandshakeRequest/Response`、`Ping/Pong`、`Cancel`、`StreamComplete`、`WindowUpdate`、`GoAway` 和 health 帧都有类型级最小/最大载荷校验。
