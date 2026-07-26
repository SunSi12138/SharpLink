# SharpLink 0.7.11 Codec Adapter 架构

English: [`en/architecture-0.7.11.md`](en/architecture-0.7.11.md)

## 目标

0.7.11 把第三方序列化从 SharpLink 核心中彻底解耦。Generator 只理解通用 Adapter 元数据，不识别 SharpPack、MemoryPack 或 NuGet 包名；Runtime 只管理 Adapter、Scope 和闭合 Codec 的生命周期。

本版本同时删除 `SharpLink.Serializer.MemoryPack`、`MemoryPackCodec`、`MemoryPackCodec<T>`、`RpcExternalCodecAttribute` 和进程级 generated Codec registry。官方复杂对象图扩展改为 `SharpLink.Serializer.SharpPack`，NuGet 依赖使用精确版本范围 `[1.1.0]`。

## 公共 SPI

序列化扩展程序集使用 `RpcCodecAdapterRegistrationAttribute` 声明：

- Adapter 实现类型；
- 稳定的 `AdapterId`；
- 稳定的 `WireFormatId`；
- 可选的第三方 selector Attribute。

SharpPack 扩展声明的身份为：

```text
AdapterId:    sharplink.serializer.sharppack/v1
WireFormatId: memorypack-binary/v1
Selector:     SharpPackableAttribute
```

`AdapterId` 表示实现与生命周期身份；`WireFormatId` 表示线上字节格式。实现可以替换而保持 wire compatible，但改变 wire format 必须触发契约不兼容。

Runtime SPI：

```csharp
public interface IRpcCodecAdapter
{
    string AdapterId { get; }
    string WireFormatId { get; }
    IRpcCodecAdapterScope CreateScope();
}

public interface IRpcCodecAdapterScope : IDisposable
{
    IRpcCodec<T> CreateCodec<T>();
}
```

Adapter 必须是无状态、线程安全的 `public sealed` 类型，并提供 public parameterless constructor。Formatter、serializer Context、配置和缓存状态全部位于 Scope；Scope 支持并发创建 Codec，`Dispose` 必须幂等。

## 编译期选择

Generator 对每个 RPC 可达闭合类型合并以下候选：

1. 类型级 `[RpcCodecAdapter(typeof(Adapter))]`；
2. 程序集级 `[assembly: RpcCodecAdapter(typeof(Target), typeof(Adapter))]`；
3. 类型上命中的已注册 selector Attribute，例如 `[SharpPackable]`。

没有候选时才尝试 SharpLink 原生 Codec。一个候选或多个相同 Adapter 候选按幂等合并；多个不同 Adapter 报 `SHARPLINK045`。安装 Adapter 包本身不会形成 fallback，也不会改变普通 DTO 的 wire format。

Adapter registration 只通过 Roslyn symbol metadata 从当前及传递引用程序集读取，不执行 `Assembly.Load`、运行时目录扫描或 Adapter 实例化。`SHARPLINK042`–`SHARPLINK049` 覆盖 registration、实现形态、selector 冲突、Attribute 用法、开放泛型、身份冲突和内置 Codec 覆盖。

生成代码直接包含闭合调用：

```csharp
adapterScope.CreateCodec<PluginGraph>()
```

生成路径不使用 `MakeGenericType`、`Activator.CreateInstance`、非泛型 serialize API 或运行时 resolver。

## Manifest API v3

`SharpLinkGeneratedManifestVersions.Api` 为 3；Protocol 仍为 2。每个 `IRpcGeneratedCodecFactory` 公开目标类型、schema、wire format、可选 Adapter 身份和闭合创建/类型校验方法。

原生 factory：

- `AdapterId` 和 `Adapter` 为 null；
- `WireFormatId` 为 `sharplink-native/v1`；
- 不接受 Adapter Scope。

Adapter factory：

- 声明 Adapter 与 wire identity；
- 必须接收对应 Scope；
- 在发布前创建并验证正确的 `IRpcCodec<T>`。

旧 Manifest API 插件不能载入 0.7.11 Runtime，必须重新编译。

Contract JSON 的 request、response、stream item 和 DTO member 都必须包含非空 `wireFormatId`。顶层必填 `codecs` 清单同时记录所有可达闭合 Codec 的类型和 wire identity，因此 `List<PluginGraph>` 等原生容器内部的 Adapter wire 变化也会报告 `SHARPLINK030`。项目尚未 1.0，因此不保留开发期临时 JSON 的缺字段兼容：缺少 `codecs`，或任一必填列表/条目/identity 为 null、空或纯空白，统一使基线无效并报告 `SHARPLINK024`。

## Scope 与事务发布

自动 Scope 的唯一粒度为：

```text
SharpLinkRuntimeContext × Manifest instance × AdapterId
```

同一 Manifest 的多个 SharpPack 类型共享一个 `SharpPackSerializerContext`；不同 Client/Server、Manifest、插件或 replace 代际不共享。

Build、register 和 replace 的候选准备在 registry lock 外执行：

1. 校验 Manifest/API/Protocol 与冲突；
2. 按 Adapter ID 创建 Scope；
3. 创建并校验全部闭合 Codec；
4. generation 未变化时原子发布 Codec/Proxy/Stub/Service 快照；
5. generation 已变化时释放整个候选并重新准备。

任一步失败都会逆序释放候选 Scope 和 service registration，运行中快照不变。第三方 Adapter 的 `CreateScope`/`CreateCodec` 不在 registry lock 内调用。

## Cache、replace 与卸载

resolved Codec cache 绑定具体 `RpcGeneratedCodecRegistration`。显式 `UseCodec` 是不可被 generated factory 替换的最高优先级；Runtime 不 Dispose 调用方 Codec 或自定义 Context。

replace 先完整准备新 Manifest 和新 Scope，再原子发布。新调用只解析新 registration；已取得租约的旧调用继续使用旧 Codec/Scope。旧模块排空后只删除属于旧 owner 的 cache entry，然后释放旧 Scope。旧清理不能误删新代 cache。

进程 Catalog 只保存 Manifest 弱引用。动态 unregister 完成后，Runtime 清除 proxy/stub/service/factory/type/manifest 强引用并释放 Scope；collectible ALC、Assembly、Type、Manifest、factory、Codec、Scope 和 SharpPack Context 均可回收。

## SharpPack 扩展

`SharpPackRpcCodecAdapterScope` 构造时创建一个冻结且非空的 Context formatter graph，所有闭合 Codec 共享它。SharpPack 1.1.0 已修复 Context formatter 绑定与递归构造，但空 Context 对非 collectible 类型仍保留进程级默认 slot 快路径；非空 graph 因此继续保证自动 Scope 不回退到该 slot，不同 Runtime/Manifest/代际拥有独立 formatter 实例。Codec 使用显式 Context 的 SharpPack API；序列化时通过零分配值类型 writer 转接把 `IBufferWriter<byte>` 变成 NativeAOT 可见的具体泛型闭合类型，不关闭 IL scanner。反序列化验证完整消费；截断、格式错误和 trailing bytes 映射为不含业务 payload 的 `SharpLinkException(DataLoss)`。已有 `SharpLinkException`、取消、致命异常及包装或聚合中的这些异常不重复包装。

高级 formatter 使用调用方 Context：

```csharp
var context = new SharpPackSerializerContextBuilder()
    .Register<ThirdPartyGraph>(new ThirdPartyGraphFormatter())
    .Build();

builder.UseCodec(SharpPackRpcCodec.Create<ThirdPartyGraph>(context));
```

Context 与 Codec 的释放责任属于调用方。
