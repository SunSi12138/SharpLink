# 迁移到 SharpLink 0.7.11

English: [`en/migration-0.7.11.md`](en/migration-0.7.11.md)

0.7.11 是 1.0 前的源码/API 破坏性迁移。Protocol v2 不变，但 generated Manifest API 升为 3，所有 Contract/Plugin 必须重新编译。

## 包和 Attribute 替换

| 旧用法 | 0.7.11 |
| --- | --- |
| `SharpLink.Serializer.MemoryPack` | `SharpLink.Serializer.SharpPack` |
| `MemoryPack` / `using MemoryPack` | SharpPack 1.0.1 / `using SharpPack` |
| `[MemoryPackable]` | `[SharpPackable]` |
| `[RpcExternalCodec]` | serializer selector 或 `[RpcCodecAdapter(...)]` |
| `MemoryPackCodec.Resolver` | 删除；自动 Manifest Adapter |
| `MemoryPackCodec<T>.Instance` | 自动 Adapter，或 `SharpPackRpcCodec.Create<T>(context)` |

删除所有 `.UseSerializer(MemoryPackCodec.Resolver)`。仅安装 SharpPack Adapter 不会改变普通 DTO；原生 Generator 能处理的类型继续使用 `sharplink-native/v1`。

## 普通 SharpPack DTO

Contract 项目同时引用 `SharpLink.Serializer.SharpPack`：

```csharp
using SharpPack;

[SharpPackable]
public partial class PluginGraph
{
    public string Name { get; set; } = string.Empty;
    public PluginGraph? Parent { get; set; }
}
```

不需要 SharpLink 额外 Attribute、resolver 或 Client/Server Builder 配置。

## 无 selector Attribute 的类型

可修改源码时使用类型级绑定：

```csharp
[RpcCodecAdapter(typeof(MySerializerRpcCodecAdapter))]
public sealed class Payload;
```

第三方闭合类型使用程序集级绑定：

```csharp
[assembly: RpcCodecAdapter(
    typeof(ThirdPartyPayload),
    typeof(MySerializerRpcCodecAdapter))]
```

开放泛型、内置 primitive 和多个不同 Adapter 绑定会在编译期失败。

## 集合边界

`List<PluginGraph>` 默认使用原生 List Codec，元素使用 SharpPack Adapter Codec。只有希望整个闭合集合使用 SharpPack wire format 时才显式绑定 `typeof(List<PluginGraph>)`。

## 自定义 formatter

```csharp
var context = new SharpPackSerializerContextBuilder()
    .Register<ThirdPartyPayload>(new ThirdPartyPayloadFormatter())
    .Build();

var codec = SharpPackRpcCodec.Create<ThirdPartyPayload>(context);
clientBuilder.UseCodec(codec);
serverBuilder.UseCodec(codec);
```

显式 Codec 优先于 Manifest Adapter。Runtime 不释放用户 Codec 或 Context。

## Contract Manifest

删除并重新生成所有开发期 contract baseline。0.7.11 要求每个 request、response、stream item 和 DTO member 都存在非空 `wireFormatId`；缺失、null、空或纯空白值报告 `SHARPLINK024`。不提供开发期 JSON 推断、warning 或迁移 fallback。

MemoryPack→SharpPack 只有在固定 golden payload 验证后才保持 `memorypack-binary/v1`。仓库测试已覆盖 null、nullable/string/非 ASCII、array/list/dictionary、nested、empty、union/polymorphism 和 circular-reference，并验证 SharpPack 1.0.1 读旧字节和写出相同字节。

## 升级检查清单

1. 替换包、namespace 和 `[SharpPackable]`。
2. 删除 resolver 与旧 Codec API。
3. 为没有 selector 的闭合类型增加 `[RpcCodecAdapter]`。
4. 重新编译全部 Contract、Service 和 Plugin。
5. 重新生成并提交带必填 `wireFormatId` 的 Contract baseline。
6. 对业务已有 payload 保留自己的 golden fixtures。
7. 运行 JIT、NativeAOT 和本地包消费测试。
