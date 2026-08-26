# 契约与序列化

## 调用形态

Generator 根据签名生成五类调用：Unary、OneWay、ClientStreaming、ServerStreaming、DuplexStreaming。一个方法可以有多个客户端流参数；每个流在请求内有独立 stream id，共享连接级接收窗口。

- 普通响应：`ValueTask<T>`。
- 无返回业务值：`ValueTask`。
- OneWay：标注 `[Oneway]`；本地成功只表示请求已被框架接受，不表示服务端业务完成。
- 客户端流：一个或多个 `IAsyncEnumerable<T>` 参数。
- 服务端流：返回 `IAsyncEnumerable<T>`。

契约继承会被完整展开。冲突签名、非法泛型、指针/ref-like 类型、无法构造的服务或 DTO 会产生 `SHARPLINKxxx` 编译诊断，而不是运行时失败。

## 原生 Codec

内置 Codec 覆盖常用 primitive、enum、string、时间/标识类型、数组、List、Memory、nullable、tuple、受支持不可变集合和由 `[RpcSerializable]`/`[RpcMember]` 描述的 DTO。编码有明确 null 标记、长度上限和完整消费检查；尾随字节、非法 UTF-8、非规范整数或 required/nullability 违反会作为 `DataLoss`。

当一个值类型没有命中共享内置 Codec、显式/生成 Codec 或 resolver，且其运行时表示不包含 managed reference 时，Runtime 可以回退到 `UnsafeBlitCodec<T>`，直接把 `Unsafe.SizeOf<T>()` 范围内的 managed representation 写入 payload。这个原始表示包含结构体 padding；它既不是 canonical field-wise 编码，也不能把普通 `new`/`default` 后的 padding 为零当作跨运行时安全保证。涉及 unsafe/native/uninitialized 来源或机密边界时，可靠的支持路径是显式绑定使用 field-wise/non-raw representation 的自定义 Codec/Adapter，而不是依赖调用方先清 padding 后再经过可能发生的 struct copy。完整边界见 [UnsafeBlit padding 安全评估](unsafe-blit-padding-security.md)；跨运行时 ABI/兼容性范围见 [UnsafeBlit 兼容性](codec-compatibility.md)。这里描述的是 RPC payload Codec，不改变 SharpLink 自身协议 framing 字段的编码。

DTO 演进规则：

- 字段 id 是 wire identity；发布后不要重用或改变含义。
- 新增可选字段通常兼容；删除字段前确认所有对端已停止发送。
- required、nullable、wire type 或嵌套 schema 变化可能不兼容。
- Generator Manifest 的兼容性 identity 为 **(Contract assembly, 闭合类型 T) → Kind / SchemaId / WireFormatId**，用于同进程注册与替换校验，不能绕过跨版本集成测试。隐式最终选择（内置 Native 路径与非托管 UnsafeBlit fallback）同样物化进 manifest，使之后的显式 Adapter/Direct 选择能被检测为 wire break。

## 自定义 Codec

Generated RPC 的 Codec 由 Contract assembly 在编译期拥有并冻结。要为某个闭合 CLR 类型指定单独 Codec，在 Contract assembly 上声明精确类型绑定：

```csharp
[assembly: RpcCodecAdapter(
    typeof(MyType),
    typeof(MyTypeCodec),
    WireFormatId = "my-type/v1")]
```

`IRpcCodec<T>` 必须完整写出一个值，并从完整 payload 解码。对端输入不合法时抛出带具体 code 的 `SharpLinkException`，通常是 `DataLoss`；不要把协议输入错误包装成 `Internal`。Codec 不能保留框架提供的输入序列或输出 writer。

同一 Contract assembly 内的所有 `[RpcContract]` 对相同闭合类型 `T` 共享同一份最终 Codec binding；不同 Contract assembly 可以为同一个 `T` 选择不同 Codec。批量路由使用 assembly 级 `RpcCodecRoute`。Client/Server builder 不提供按实例覆盖 generated RPC wire Codec 的 `UseCodec<T>`。

`UseSerializer(Func<Type, IRpcCodec?>)` 是实例级 Runtime Context fallback resolver，仅用于未被 generated Contract assembly frozen graph 接管的运行时解析；它不会覆盖 generated RPC 的最终 wire Codec binding。

## Codec Adapter 与 SharpPack

`IRpcCodecAdapter` 用于由 Generator 生成闭合工厂，再由 Runtime Context 创建隔离 scope。Adapter identity、wire-format identity 和 schema identity 都参与注册兼容性判断。

官方复杂对象图扩展是 `SharpLink.Serializer.SharpPack`。用 `[RpcCodecAdapter(typeof(SharpLink.Serializer.SharpPack.SharpPackRpcCodecAdapter))]` 或项目约定把类型交给 SharpPack；每个 Runtime Context × Manifest × AdapterId 拥有独立 scope，不使用进程级默认 formatter slot。动态模块排空后，Codec、Adapter scope 和 collectible ALC 才能一起释放。

## 协商压缩

Client 与 Server 在各自 `UseRuntime` 中按偏好顺序注册 provider：

```csharp
builder.UseRuntime(options =>
{
    options.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());
    options.Compression.MinimumPayloadBytes = 2048;
});
```

只有双方 wire profile 完全匹配才启用压缩；单边配置或无交集会安全退回原始帧。压缩只覆盖业务 payload，协议路由前缀保持可解析。只有同时达到最小 payload、绝对节省和比例节省阈值才发送压缩结果。解压输出仍受协商后的最大 frame payload 限制。

运行证据：`demo/Compression` 用不同 Brotli 编码级别、相同 wire profile 完成双向压缩并统计 provider 调用。
