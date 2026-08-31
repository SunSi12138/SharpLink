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

内置 Codec 覆盖常用 primitive、enum、string、时间/标识类型、数组、List、Memory、nullable、tuple、受支持不可变集合和由 `[RpcSerializable]`/`[RpcMember]` 描述的 DTO。编码有明确 null 标记、长度上限和完整消费检查；尾随字节、非法 UTF-16LE 字节长度、非规范整数或 required/nullability 违反会作为 `DataLoss`。

其中一小组类型属于 **Framework wire primitive**：SharpLink 直接定义并拥有其固定 wire semantic，因此它们不是可配置 Codec policy surface。当前包括 primitive numerics、`bool`、`char`、`string`、`Guid`、SharpLink 明确定义固定 wire semantic 的时间/标识 scalar、enum，以及作为 protocol bytes primitive 的 `byte[]`。这些类型不能通过 `RpcCodec`、`RpcCodecAdapter` 或 `RpcCodecRoute` 重绑定。

普通 `T[]`（`byte[]` 除外）、`List<T>`、`Dictionary<K,V>`、Tuple/ValueTuple、DTO/record 和普通 user struct/class 不属于 Framework wire primitive。它们即使默认实现使用 generated/native/blit fast path，也仍然是 configurable payload type。换言之：**fast path != primitive != policy immutability**。

当一个值类型没有命中共享内置 Codec、显式/生成 Codec 或 resolver，且其运行时表示不包含 managed reference 时，Runtime 可以回退到 `UnsafeBlitCodec<T>`，直接把 `Unsafe.SizeOf<T>()` 范围内的 managed representation 写入 payload。这个原始表示包含结构体 padding；它既不是 canonical field-wise 编码，也不能把普通 `new`/`default` 后的 padding 为零当作跨运行时安全保证。涉及 unsafe/native/uninitialized 来源或机密边界时，可靠的支持路径是为该 **user-defined payload type** 显式绑定 field-wise/non-raw representation 的自定义 Codec/Adapter，而不是依赖调用方先清 padding 后再经过可能发生的 struct copy。完整边界见 [UnsafeBlit padding 安全评估](unsafe-blit-padding-security.md)；跨运行时 ABI/兼容性范围见 [UnsafeBlit 兼容性](codec-compatibility.md)。这里描述的是 RPC payload Codec，不改变 SharpLink 自身协议 framing 字段的编码。

DTO 演进规则：

- 字段 id 是 wire identity；发布后不要重用或改变含义。
- 新增可选字段通常兼容；删除字段前确认所有对端已停止发送。
- required、nullable、wire type 或嵌套 schema 变化可能不兼容。
- 当前 Generator Manifest 仍沿用 `SchemaId` / `WireFormatId` 作为既有 generated registration 与 baseline infrastructure；#386 只负责确定 assembly-owned final Codec graph，不把这些字符串扩展成新的 per-type compatibility model。后续 #396 会以 fixed-width `CodecHash` / `RpcAssemblyHash` 替换长期 identity 模型并执行 assembly-level exact equality。

## 自定义 Codec

Generated RPC 的 Codec 由 Contract assembly 在编译期拥有并冻结。对非 Framework wire primitive 的闭合 CLR 类型，手写 `IRpcCodec<T>` 只通过 `RpcCodec` 精确绑定。当前 dev 仍要求 Codec 用 `RpcCodecImplementation` 提供 legacy wire/schema registration identity；这不是 #386 新定义的长期 compatibility API，后续由 #396 的 hash identity 模型替换：

```csharp
[assembly: RpcCodec(typeof(MyType), typeof(MyTypeCodec))]

[RpcCodecImplementation("my-type/v1", "my-type-schema/v1")]
public sealed class MyTypeCodec : IRpcCodec<MyType>
{
    // ...
}
```

`RpcCodecAdapter` 只用于精确选择一个已注册的 `IRpcCodecAdapter`；`RpcCodecRoute` 只用于按 `Managed` / `Unmanaged` scope 批量选择 Adapter。不存在另一条通过 `RpcCodecAdapter(... WireFormatId = ...)` 绑定手写 `IRpcCodec<T>` 的 Direct API。

`IRpcCodec<T>` 必须完整写出一个值，并从完整 payload 解码。对端输入不合法时抛出带具体 code 的 `SharpLinkException`，通常是 `DataLoss`；不要把协议输入错误包装成 `Internal`。Codec 不能保留框架提供的输入序列或输出 writer。

同一 Contract assembly 内的所有 `[RpcContract]` 对相同闭合类型 `T` 共享同一份最终 Codec binding；不同 Contract assembly 可以为同一个 configurable `T` 选择不同 Codec。批量路由使用 assembly 级 `RpcCodecRoute`，scope 只有 `Managed`、`Unmanaged` 与它们的组合 `All`；不存在 `Native` route。Framework wire primitives 永远不参与 routing。

如果确实需要为 `int`、`string`、enum 等 Framework wire primitive 定义不同 wire representation，应创建 user-defined wrapper struct/class，并为 wrapper 配置 Codec。这样 final graph 仍保持每个 closed `T` 唯一，同时不会把所有 framework primitive 暴露成 configurable policy surface。

`UseSerializer(Func<Type, IRpcCodec?>)` 是实例级 Runtime Context fallback resolver，仅用于未被 generated Contract assembly frozen graph 接管的运行时解析；它不会覆盖 generated RPC 的最终 wire Codec binding。

## Codec Adapter 与 SharpPack

`IRpcCodecAdapter` 用于由 Generator 生成闭合工厂，再由 Runtime Context 创建隔离 scope。用于 generated RPC 的 Adapter 实现必须声明 `[RpcCodecSemanticIdentity(high, low)]`。对一个闭合目标类型 `T`，最终 Adapter `CodecHash` 把这份显式的 Adapter semantic identity 与 `T` 的 canonical type identity 组合成一个 **opaque compatibility boundary**；Generator 不会遍历 `T` 的字段、属性或 DTO member graph 去猜测第三方 serializer 的 wire schema。

因此，仅修改 Adapter 目标类型的 CLR 成员不会自动改变该 Adapter 的 `CodecHash`。当 Adapter 的实际编码、解码、schema evolution 规则或任何会改变 wire compatibility 的行为发生变化时，Adapter 作者必须显式 bump `[RpcCodecSemanticIdentity]`。反过来，保留同一 semantic identity 就是在声明这些 closed Adapter Codec 仍然 wire-compatible。不同目标类型即使使用同一个 Adapter，也会因为 canonical target type identity 不同而得到不同的 closed `CodecHash`。

`AdapterId` 继续负责 Adapter 注册/选择和 Runtime scope ownership；它不是目标成员图的替代 schema hash。不要通过反射目标类型布局或字段集合来推导 Adapter wire identity，因为 Adapter 可以忽略、重命名、转换或以完全不同的 schema 编码这些成员。

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