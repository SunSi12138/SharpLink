# #775 N0：单一契约通道合同冻结

状态：**N0 文档合同；产品尚未实现。N0 合并不授权实施 N、采用历史 C/D、运行资格或性能实验，也不关闭 #775/#805。**

本页是后续采用工作的唯一 N0 入口。它细化 [9393 选定设计](https://github.com/SunSi12138/SharpLink/blob/9393a3ff58936da16a7c2fcd2a0c679df7a92e76/doc/issue-775-clean-contract-channel-design.md) 与 [2026-10-31 决定](https://github.com/SunSi12138/SharpLink/issues/775#issuecomment-6541096239)，不改变单模式、五族、原身份与物理退出要求。数值、排队 cut 和迁移处以本页为准；历史报告仍按自己的源码、输入和接受边界引用，不能改写成新产品结果。

## 1. 来源、收益与发布原子性

| 名称 | 精确来源 | 用途 |
|---|---|---|
| P | dev `416f71fe3bf39f3fb9444e7c7492c7f007918113` | 唯一实施起点及未来成本基线；API5、wire2.4、ContractId 路由。含 #984 input、#987 terminal、#988 output join。 |
| S7 | `d9b66b4bfc9fcd0f48ec34a8f06cb7616833f557` | 第七非发布 profile 的 count/discard/credit/ALC 算法与正负证据；不是可发布依赖。 |
| C | `0c236531efb694fbc8be0e9cdc1f5fa49a6a8534` | 未发布集成参考。其 API6、minor5、frame17–22不是已发布分配；已知物理尾部缺口不因 P 修复自动消失。 |
| D | `db2bcc8d5ce43320b1c00b3e828dc08641ff5f7b` | 唯一值 owner/borrow 的算法参考，仍 HOLD；不能修 C 取得绿色后恢复 D 比较。 |
| N | 尚无源码 subject | 将来另获授权、从 P 建立的单模式采用包；不整包恢复 C，不混入 #985 hold 产品。 |

用户收益：控制连接完成 Register/Replace、数据 Session 的 Manifest 尚旧时，以同一显式 site、原 receipt/requirement 和有限 Prepare 取得准确注册；普通调用只使用对应物理 Session 的 token，不误中另一个 R。无已确认目标时在业务序列化/producer 前 FailFast。

最小**可发布**单元是 receiver + 完整 facts/组合 generated ABI + 双方 Session/wire/组 cut + 五族准入/物理 owner + 全 caller/包迁移。以下是非发布开发检查点，不是可逐个合并到 dev 的半成品模式：

1. 源码/迁移/guard/owner 图与本合同一致；旧入口移除计划完整。
2. static Unary/OneWay 的本地 factory 接通真实 bounded Bind、exact token dispatch；其他族暂未接通只能存在于非发布分支，不能冒称完整 API。
3. 组/receipt/ABA、五族、动态本地代次、物理退出及全部 caller 接通。
4. 同一最终 HEAD 完成独立 #805、实际包/平台、canonical 与预注册成本门槛后，才允许原子采用 PR 合并。

P 没有 BoundInvocation。历史 O160d/C0c236/tool914390 的内部 NativeAOT 同步模块 Unary 72→112 B/op，已由实际 `new BoundInvocation`/EEType40B归因；不是当前可删的40B，不是公开 NativeAOT RegisterAssembly 能力，也不是 N 的成本预测。N 不预选 D 的 packed 布局；不得复制 release authority、提前释放、reacquire retired gate 或查 latest 来取得数字。

## 2. 精确公开接收者

以下是 **N 目标签名摘要，不是当前可调用 API或完整可编译实现**；constructor bodies省略不代表新增public构造能力。新类型位于 `SharpLink.Abstractions`；`IService`、`SharpLinkMetadata` 保持现有 `SharpLink.Sdk` 命名空间/Abstractions 实体与 SDK forwarding。未列生命周期、配置、注册、health、可见性等待成员保留原合同。

```csharp
public interface ISharpLinkClient : ISharpLinkAssemblyRegistry, IAsyncDisposable
{
    IRpcRuntimeContext RuntimeContext { get; }
    RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null)
        where TContract : IService;
    RpcLocalContract GetLocalContract<TContract>() where TContract : IService;
    RpcLocalContract GetLocalContract(System.Reflection.Assembly registeredAssembly, long contractId);
    IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null);
    // Existing non-Get lifecycle/control members remain.
}

public interface ISharpLinkMultiClusterClient : IAsyncDisposable
{
    RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null)
        where TContract : IService;
    // Existing non-Get coordinator/control members remain.
}

public sealed class RpcLocalContract
{
    internal RpcLocalContract(Guid issuer, long generation, long contractId, RpcHash128 hash);
    // Internal readonly scalar fields/properties only; no public constructor/setter.
}

public sealed class RpcProxyOptions
{
    public SharpLinkMetadata? Metadata { get; init; }
    public RpcRegistrationReceipt? Receipt { get; init; }
    public RpcRegistrationRequirement Requirement { get; init; } // default Compatible
}

public enum RpcRegistrationRequirement
{
    Compatible = 0, MinimumVersion = 1, ExactRegistration = 2
}

public readonly record struct RpcRegistrationReceipt(
    Guid ServerInstanceId, Guid RegistryEpoch, long Generation,
    long ContractId, RpcHash128 RpcAssemblyHash, long RegistrationId);

public sealed class RpcProxy<TContract> where TContract : IService
{
    internal RpcProxy(TContract service, IRpcContractChannel channel);
    public TContract Service { get; }
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IRpcContractChannel
{
    IRpcRuntimeContext RuntimeContext { get; }
    ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default);

    ValueTask InvokeOneWayAsync<TRequest, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        in TStreams streams, SharpLinkMetadata? metadata, CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter;

    ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default) where TStreams : struct, IRpcClientStreamWriter;

    IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method, in TRequest request, IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec, in TStreams streams, SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default) where TStreams : struct, IRpcClientStreamWriter;
}
```

这些接口没有传入远端 BindingId/Session 的入口。第三方可实现自己的 IRpcContractChannel，但不因此取得 SharpLink 内部 capability；内建 generated/manual 调用都直接进入同一个 contract site，不能通过 raw adapter 进入第二管线。RpcProxy 只配对 Service 与同一个 channel；调用 Service 不增加一层 RpcProxy async forwarding。

### 2.1 本地构造、scope 与所有权

- 两种 factory 同步、本地、不等待 Connect/Manifest/Bind；它们在成功返回前原子提交有界 demand。连接前创建也成立，调用仍可 NoBoundRoute。失败撤销本次未提交 demand/cache 配额，不遗留半个 site。
- RpcLocalContract 冻结 issuer GUID、正的 monotonic local-generation、ContractId、完整 hash，均 host scalar；无 Type/Assembly/Codec/Stub/delegate。generation 不能回绕、复用或随无关 snapshot clone 变化；耗尽拒绝新本地注册，不重置旧 selector。不同 client、child generation 或退休 registration 的 token 失败，不自动升到 latest。
- Assembly 重载只接受已注册的**同一个 Assembly 对象**及其唯一当前 contract；在本地原 module 保护下解析。不存在/歧义/retired/非 owner 失败，不扫描另一个 owner 的同数字 ContractId。Assembly 是冷输入，不进返回 token 或控制缓存。
- scope 来自 issuer/client 的 endpoint/cluster/配置的 tenant 上下文；MultiCluster factory 只捕获一次当前 route/child generation，Service 与 Prepare 都不再经过 coordinator 重选。手写调用使用明确的 ISharpLinkClient；不新增 coordinator-wide identity-neutral raw 通道。
- site key = issuer/child scope + local generation + ContractId/hash + **完整** requirement/receipt。metadata 不参加远端 Bind 身份；它是现有 immutable SharpLinkMetadata wrapper，per-call metadata 非 null 时覆盖固定 metadata，不合并/改写租户权限。认证/授权/interceptor仍逐请求决定，receipt/token不是凭证。
- options 为 sealed/init-only，metadata 自身复制输入数组且不可变，receipt为纯值；factory捕获这些值。动态 channel 可合法持有本地 module/Codec；控制缓存弱引用 site，Bind/waiter/retired snapshots 只保留 scalar/control identity，完成后清理。RuntimeContext归 client/独立创建者所有，proxy/channel不另行 Dispose它。

**本地 factory guard（A1）**：null/非法结构或 enum/receipt/metadata大小 → client/coordinator terminal → issuer/精确local generation → descriptor/hash/Codec ownership → 原子 demand/cache/wrapper。无网络错误优先于这些步骤。receipt要求两个GUID非空、Generation≥0、RegistrationId>0、非零ContractId/非空hash，并与local ContractId/hash一致；无receipt只能Compatible。foreign/retired不能通过另查latest成功。

null local/Assembly 为ArgumentNullException；非法enum/timeout/metadata长度为ArgumentOutOfRangeException；缺必需receipt或结构无效为ArgumentException。结构有效但receipt与local不符走W6 InvalidLocalContract；foreign/absent/retired按W6分类。client终态沿用ConnectionClosed，coordinator拒绝factory沿用其InvalidOperationException生命周期边界。Prepare对退休local用LocalContractUnavailable；已逃逸dynamic receiver调用仍保留P wrapper的Unavailable及原guard时机。手写Codec的schema/语义是应用注册合同，不声称typed参数或hash可密码学证明任意自定义Codec正确。

**Prepare（A2）**：显式 timeout 必须 `0 < timeout <= SharpLinkTimer.MaximumDelay`；无无限/无参重载。invalid timeout → caller cancellation → client/local终态 → 一个原 monotonic readiness deadline。Ready completion提交前再次检查原budget、cancel和generation。已Ready同步完成、不建新Bind；一个waiter取消不取消其他waiter或共享Bind。重连/refresh/receipt/醒来都不重置原budget；无准备者也可由有界demand取得route。Prepare不预留call容量或延长registration寿命。

### 2.2 receipt 的生产与比较

ISharpLinkServer新增以下声明；自定义实现可用默认 `NotSupportedException` 明确不支持，不能先调用旧操作、再查询latest来伪造本次receipt：

```csharp
RpcRegistrationReceipt GetRegistrationReceipt(System.Reflection.Assembly registeredAssembly, long contractId);
SharpLinkServerAssemblyRegistrationResult RegisterAssemblyWithReceipts(System.Reflection.Assembly assembly);
ValueTask<SharpLinkServerAssemblyReplacementResult> ReplaceAssemblyWithReceiptsAsync(
    System.Reflection.Assembly oldAssembly, System.Reflection.Assembly newAssembly,
    TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);

public sealed class SharpLinkServerAssemblyRegistrationResult
{
    internal SharpLinkServerAssemblyRegistrationResult(
        SharpLinkAssemblyRegistrationResult registration, IReadOnlyList<RpcRegistrationReceipt> receipts);
    public SharpLinkAssemblyRegistrationResult Registration { get; }
    public IReadOnlyList<RpcRegistrationReceipt> Receipts { get; }
}
public sealed class SharpLinkServerAssemblyReplacementResult
{
    internal SharpLinkServerAssemblyReplacementResult(
        SharpLinkAssemblyReplacementResult replacement, IReadOnlyList<RpcRegistrationReceipt> receipts);
    public SharpLinkAssemblyReplacementResult Replacement { get; }
    public IReadOnlyList<RpcRegistrationReceipt> Receipts { get; }
}
```

结果list为不可变只读拷贝，按ContractId排序；失败/仅contract无service时为空。receipt在该次publication不可逆commit前预备并随commit捕获，hash来自contract owner、RegistrationId来自service R；动态service查询传service module Assembly，不是另注册的contract Assembly。Getter只是当前精确Assembly/R快照，不代表某次先前Replace。Register/Replace原API保留，无receipt能力也不从共享client/server结果类型添字段。

Compatible验证contract/hash和同S/epoch（有receipt时）；MinimumVersion还要求 **该R自己的** publication generation≥receipt.Generation；ExactRegistration还要求同RegistrationId。不得以无关注册推进的global generation满足约束。ID/generation为非负/正Int64、单epoch单调、不回绕；耗尽在commit前拒绝，不能重用旧RID。receipt可以跨控制/数据连接传递，不持call lease、不保活ALC。调用方取消只取消等待；发布/旧清理继续，取消返回不伪造已返回receipt结果。（A3）

## 3. 完整 facts 与组合 generated ABI

继续用 `RpcMethodDescriptor`，不增加 per-method handle 层。它是一个 **32B、无引用的readonly scalar value**：ContractId:i64、MethodId:i64、timeoutTicks:i64、ClientStreamCount:i32、Kind:u8、flags:u8、padding2。flags bit0 response payload、1 client streams、2 timeout declaration、3 idempotent、4 response nullable、5 nullable timeout value present、6 supports cancellation；bit7保留为0。保持现有constructor参数/default/init属性，新增 `bool SupportsCancellation { get; init; }`；fullticks不截毫秒。null timeout canonical ticks0，nullable presence、声明presence独立，零/负值含义不因存储压缩被改写。无Type/Assembly/Stub/Codec/Name/delegate。（F1）

冷合法性：非零匹配contract/method；Kind已定义；count0..127且HasClientStreams==(count>0)；Unary无输入流，OneWay无response payload，ClientStreaming/Duplex必须有输入，ServerStreaming无输入且有response payload，Duplex有response payload；所有stream形态（含OneWay input）的SupportsCancellation必须true。HasResponsePayload/ResponseNullable与生成返回类型一致；完整descriptor与manifest facts全值相等，SupportsCancellation还包括显式用户token。未知method只有required resolver返回false；默认值不是合法已知facts，不制造Unary fallback。无RPC的custom codec manifest不因此被要求提供Stub。timeout储存可忠实表示null/零/负ticks，调用时仍按P原selector验证显式非正timeout并拒绝；不靠压缩存储把非法值变合法。（F2）

```csharp
public interface IRpcStub
{
    long InterfaceHash { get; }
    bool TryResolveMethod(long methodId, out RpcMethodDescriptor method);
    ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
        in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args);
    ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken);
    ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge,
        in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output);
    ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
        in RpcMethodDescriptor method, long requestId, ReadOnlySequence<byte> args,
        IBufferWriter<byte> output, CancellationToken cancellationToken);
}
public interface IRpcGeneratedServerBridge
{
    void EnsureUserCodeEntry(long requestId);
    IAsyncEnumerable<T> CreateInboundStream<T>(long requestId, ushort streamId,
        IRpcCodec<T> codec, bool payloadNullable, CancellationToken cancellationToken);
    ValueTask PumpOutboundStreamAsync<T>(long requestId, ushort streamId, IAsyncEnumerable<T> stream,
        IRpcCodec<T> codec, RpcMethodDescriptor method, CancellationToken cancellationToken);
}
```

resolver只在冷Bind构建该R的方法表时调用；查到false拒绝unknown，已知但非法facts拒绝Bind。每请求从捕获entry取一份完整facts，经admission、queue、persistent decode、interceptor、mapper、telemetry、Stub、bridge和异步尾部传递；不事后resolver/SupportsCancellation/MethodId→latest重查。Stub可按已知MethodId选生成case，但必须消费已解析facts，不重新恢复窄元数据。按值bridge允许async实现，四Stub的in保持只读泛型语义。（F3）

“同一facts”指同R的同一cold method entry及全值相等的readonly descriptor；值传递不要求ReferenceEquals，也不新增每call facts对象。Bind构建期间读取collectible metadata需要原R的临时冷保护，外部resolver在不可逆publish之外执行并最终重验；这不是给闲置binding预留长期call lease。

generated声明同时迁移：

- `SharpLinkGeneratedMethodDescriptor` 保留现有七个positional参数，再添加required第八个 `RpcMethodDescriptor Facts`；Kind/SupportsCancellation/MethodId必须与Facts一致。
- `SharpLinkGeneratedContractDescriptor.ProxyFactory` 精确改为 `Func<IRpcContractChannel, IRpcCodecProvider, object>`；StubFactory仍 `Func<IRpcCodecProvider, IRpcStub>`。factory/ReferencedManifestBootstrap/locator/codec owner validation一起改，不能把旧factory强转成新receiver。
- 取消optional `TryGetMethodDescriptor` 和独立 `SupportsCancellation` 的默认SPI；四个旧MethodId invocation签名移除。旧手写/外部Stub必须重新实现完整resolver/四签名；负编译oracle必须精确命中缺成员/签名而非无关错误。（F4）
- SDK只保留既有SDK命名空间类型的TypeForwardedTo（含IService/metadata/attributes），不把新Abstractions实体搬到SDK造双定义；更新包依赖、生成器标记、reference/implementation与forwarder验证。

## 4. 身份和 wire6：N0 分配决定

| 项 | P实际 | N目标（尚未实现/发布） | 理由/拒绝oracle |
|---|---|---|---|
| Generated Api | 5 | **6** | 组合breaking ABI，非只改channel名字。 |
| 精确AbiIdentity | `sharplink-3.0-api5-owner-bound-unsafe-blit-v1` | **`sharplink-3.0-api6-contract-channel-v1`** | 与C的`…api6-contract-binding-v1`也不同；先metadata preflight，旧/missing/错误identity在manifest/Stub构造前拒绝。 |
| Manifest Protocol整数 | 2 | **2** | 表示协议major，不冒充negotiated minor。 |
| Live Magic/header | 0x89 / 15B | **0x89 / 15B** | 固定header形状保留。profile BA/BB/BC在framing前拒绝。 |
| minor / compatibility floor | 4 / 4 | **6 / 6** | 有意越过未发布C的5，使其不能与N误握手；不声称5曾发布。现有正式源无6冲突。 |
| 普通prefix | ContractId64 + MethodId64 | **BindingId64 + MethodId64，16B** | 相同长度不代表兼容；handshake先隔离旧4/5和历史36B grammar，再解析业务。 |

wire6没有legacy协商fallback；成功双方必须满足minor≥6且≤本地支持上限，缺required ContractManifest能力不能进入Binding-ready。既有Metadata/Compression/FlowControl等capability位及适用形态保留，不额外强制FlowControl或复用位；未协商credit时不能伪造WindowUpdate语义，但route/queue内存界仍生效。新Bind能力由wire6强制，无新增可选“旧路由模式”。本页不执行版本常量/公共声明修改。（W1）

### 4.1 握手与Manifest身份

为兑现“同实际数据Session成功握手”的S/epoch，成功 `ProtocolV2HandshakeResponse` 在三个limit字段后增加 **ServerInstanceId:Guid16、RegistryEpoch:Guid16**，再接既有 `CompressionProfileLength:u8 + ASCII`。固定部分22→54B，整payload55..55+MaxAsciiBytes；不在错误handshake payload使用此形状。对应record在CompressionProfile可选尾参前增加两个required Guid参数。GUID按现有ProtocolV2 `Guid.ToByteArray()/new Guid(span)`字节序编码；整数LE；不使用内存布局/字符串格式。（W2）

ServerInstanceId同该ISharpLinkServer的SessionRefresh authority，RegistryEpoch在该registry lifetime固定、重建必须换值，均非空。Session成功握手后冻结两者；同server/epoch也不能移植token到另一个物理Session。ContractManifest在原generation:i64/count:i32之后附同两GUID，再接原有序contract/hash entries（44+24×count）；任何不一致是ProtocolViolation。receipt主动Bind可在条目仍旧时发生，但不能在未验证的S/epoch或错误hash资格上借路；普通非receipt目标仍由Manifest派生兼容性。认证/transport信任原义不变，GUID不是密码学凭证。

### 4.2 控制/请求帧表

除明确列出外新控制帧flags=None且禁止业务压缩。payload所有长度先做checked验证；correlation域按frame区分，Bind operation不与业务request ID混表。每个完整token=高32位incarnation（1..UInt32.MaxValue）+低32位one-based slot（1..64）；完整64位全比较，不能只看slot。（W3）

| Type | 方向 / header correlation | 精确payload / flags |
|---|---|---|
| 17 BindContract | C→S / operation:u64>0 | 40B：ContractId:i64、hash.High:u64、hash.Low:u64、Requirement:i64(0..2)、HasReceipt:i64(0/1)。有receipt则112B，追加S:Guid16、epoch:Guid16、Generation:i64、ContractId:i64、hash.High/Low:u64各一、RegistrationId:i64；无尾随字节。 |
| 18 BindAck | S→C / 同operation | 40B：Status:i64、ContractId:i64、Token:u64、RegistrationId:i64、该R PublicationGeneration:i64。成功token的slot/incarnation合法、RID>0、generation≥0；失败后三字段全0。无字符串/业务payload。 |
| 19 BindingInvalidated | S→C / 0 | Count:i64(1..64)+Count×完整token:u64，16..520B；同帧重复token拒绝。 |
| 20 BindingRelease | C→S / 0 | 一个完整token:u64（8B）。不返回业务Ack。 |
| 21 RequestAccepted | S→C / 真实requestId>0 | 空payload，flags=None；只允许ClientStreaming/Duplex，每request至多一次start claim；不是业务response。 |
| 22 OneWayInputRequest | C→S / requestId>0 | Count:u8(1..127)+下述ordinary envelope；OneWay必置，禁止HasReturn/Error/Truncated。count与facts必须相等。 |
| 4 Request | C→S / requestId>0 | ordinary envelope；OneWay count0用此帧，其他四族也用此帧，flags/shape和facts相符。 |

ordinary envelope依次是BindingId:u64、MethodId:i64、可选原TimeBudget、可选原metadata、typed business bytes；前缀、count、TimeBudget、metadata均不压缩，Compressed只改变业务字节段。TimeBudget仍按P的构帧采样/入队前核deadline/入队后不撤回规则；已入队OneWay完成仍不是远端完成。旧peer在handshake被隔离；N Session的前8B只按完整token验证，绝不按长度猜测/回退解释ContractId。恶意新peer若发送恰为有效token的64位值，那在新grammar中就是token，不承诺辨认发送者的旧格式意图或提供密码学凭据。count不从HasReturn、metadata或token推断。（W4）

已知method要求OneWay flag恰与Kind一致；HasReturn恰为HasResponsePayload且Kind不是ServerStreaming/Duplex。Cancellable保留P按实际调用控制产生的含义，不强制等于SupportsCancellation。Type22已知count冲突、Type4 OneWay遗漏已知输入count均为ProtocolViolation；未知binding/method的合法Type22依独立count安装有界discard，不查latest/Codec。OneWay普通拒绝只作原日志/资源cleanup，不新增response/Ack。

RequestAccepted在pending entry/head发布前已建立关联：即使信号早到，也必须同时满足head publication已确认、Ack已收、原pending generation未终态且原budget仍有效，才能赢一次producer start claim。known pending Unary/OneWay收到此帧是ProtocolViolation；同一合法pending的重复信号不重启producer。已终态旧request的late信号不复活，未知未来request ID拒绝；不建无界completed-ID表来追溯旧形态。

BindAck Status固定：0成功；1 ContractMissing；2 HashMismatch；3 DynamicUnsupported；4 Capacity；5 Unavailable；6 UnknownBinding；7 UnknownMethod；8 UnsupportedShape；9 Retired；10 NoBoundRoute；11 ReceiptMismatch。Bind请求不接受客户端指定slot；unknown/out-of-range status、非法shape、错误op/长度/flags为ProtocolViolation。正常容量/不兼容/退休是结构化拒绝，不靠Message匹配。

### 4.3 operation、发布与迟到结果（W5）

1. 客户端在可能暴露bytes前预装Publishing operation关联和全部quota；ID是该Session的单调u64，自1开始，不复用/不回绕。相同(contract/hash/full requirement/receipt)只合并同一个inflight Bind；不同operation成功不能共享creator token。按ID顺序发布（前一项入队结果已知后再发布下一项），服务端允许gap但拒绝0/倒退/重复Bind ID。
2. 服务端在与组cut同一cold边界捕获并核R/hash/receipt/facts、安装entry，之后才Ack；不激活service、不解业务Codec、不预留call lease。所有fallible工作遵守第6节，不能锁内调用外部getter/Codec。Draining拒绝新Bind，不因旧Ack发送晚而重新开放R。
3. early Ack只暂存于该有界Publishing operation，**入队确认前不发布route**。确认后再验原operation deadline、Session、local generation、receipt与retired-watermark；只有全部成立才发布BoundTarget(Session引用+本地Session generation+token)。
4. waiter取消不撤共享Bind。operation的独立10秒budget含排队/构帧/backpressure/Ack；不因Prepare或retry重启。可证明未发布的失败清理writer/record后归还quota。已发布或发布结果不确定而到期/关闭，禁止再发布目标，进入该Session的有界terminal reconciliation；不能把operation record释放后留下可无限累积的server token。
5. reconciliation未能在原operation budget内确定撤销时关闭**该Session**，由现有Session物理drain回收slot/control writer；不创建延寿retry任务，不把物理未退出算quota已还。该控制失败不改变其他Session/tenant预算；现有活动RPC的connection terminal保持#987 canonical cause。重连只能产生新Session/新Bind，Prepare仍用原budget。
6. completed operation不保留无界ID tombstone：只保留last-issued high-water和固定64-slot retired-incarnation watermarks。已成功operation的重复Ack仅在与已保存确认身份一致时无效；未知未来ID、冲突Ack是ProtocolViolation。已终态旧ID的late Ack不复活route；处于关闭/reconciliation的Session不再采用它。invalidated先到时更新watermark，后来Ack不能覆盖；旧Release/Invalidate不影响更新incarnation。
7. slot Release/retire先关闭业务gate；最后physical reader退出后detach R/facts/Codec，再归还binding quota。incarnation=max的slot永久耗尽不复用，64个都耗尽则Capacity并通过新Session恢复。operation ID耗尽在暴露bytes前拒绝/换Session；不得回零。

### 4.4 错误标识（W6）

不新增错误enum或解析字符串；在既有 `SharpLinkErrorDetails` 下新增public static class **ContractBinding**，public const ushort如下，数值在所列Code的namespace内稳定。wire Ack status按同名映射；无该语义的status不能用于Bind请求成功。

| Constant / DetailCode | SharpLinkErrorCode | 使用点 |
|---|---|---|
| ForeignLocalContract=60001 | FailedPrecondition | foreign issuer/child |
| LocalContractUnavailable=60002 | FailedPrecondition | absent/ambiguous/retired local generation |
| InvalidLocalContract=60003 | FailedPrecondition | 本地descriptor/hash/Codec owner或receipt/local冲突 |
| NoBoundRoute=60004 | Unavailable | valid site无confirmed route |
| ContractMissing=60101 | Unimplemented | Bind无contract |
| HashMismatch=60102 | FailedPrecondition | Bind hash不匹配 |
| DynamicUnsupported=60103 | FailedPrecondition | 平台不支持此动态能力 |
| Capacity=60104 | ResourceExhausted | 表/operation/demand/waiter硬界 |
| Unavailable=60105 | Unavailable | 非Ready/closing peer |
| UnknownBinding=60106 | FailedPrecondition | 完整token不存在 |
| UnknownMethod=60107 | Unimplemented | entry无method |
| UnsupportedShape=60108 | FailedPrecondition | 已知调用的形态不匹配（非法framing另ProtocolViolation） |
| Retired=60109 | Unavailable | 旧entry/group拒绝新准入 |
| ReceiptMismatch=60111 | FailedPrecondition | S/epoch/RID/generation约束失败 |

Prepare/operation实际timeout仍DeadlineExceeded，caller cancellation仍OCE；不造新的wire DeadlineExceeded承诺。malformed frame沿用ProtocolViolationReason。新增 `SharpLinkErrorDetails.ResourceExhausted.StreamRouteCapacity=15` 用于本地stream-state总界；这不复用现有1..14。

## 5. 有界资源账本与归还规则

这些是 **N0 新目标限额**，不是声称P已有或私有profile已验证到相同容量。默认=硬界的项为endpoint内部常量，不新增可关闭的public选项；C中的64-slot/256-method/10秒控制作为机制来源，生产aggregate值按P默认1024 accepted connections、65536 server calls缩放，而不照搬C的128-binding/64-operation小实验总量。下游不得在运行后改变限额或以另一个epoch悄悄扩容。（Q1）

endpoint指一个ISharpLinkClient或ISharpLinkServer实例及其全部物理Sessions；不是全进程静态表，不因共享RuntimeContext而串扰别的endpoint。MultiCluster各child有各自endpoint账本，coordinator不另建Bind pipeline。

| 内部字段/已有配置 | 默认 / 硬界 | 预留、拒绝与归还 |
|---|---|---|
| BindingSlotsPerSession | 64 / 64 | fixed slot array和64个retired水位；inflight binding reservation+confirmed+retiring physical entry合计≤64。满额Capacity，不按恶意slot值分配。 |
| BindingsPerEndpoint | 65536 / 65536 | 所有Session角色表项合计（client与server各有独立账本）。client在operation前reserve，成功转confirmed，失败/invalidated/Session close后物理清理归还；server在entry install前reserve，retire/release且physical readers退出、payload detach后归还。不把闲置Bind当call lease。 |
| MethodsPerBinding | 256 / 256 | cold install前检查，超限Capacity；不截断方法/只发布前256个。注册/standalone Codec能力不受此表截断，但超限契约不能在N取得RPC binding，这是明确的N容量合同。 |
| BindOperationsPerSession | 64 / 64 | Publishing、await Ack、reconciliation均计数，先reserve后record/task/frame；同key合并不重复reserve。 |
| BindOperationsPerEndpoint | 1024 / 1024 | 包括server异步Ack和client发送/等待；一次任务贯穿物理writer/flush cleanup后归还，不按caller逻辑终态提前归零。无operation配额的Capacity拒绝只能走现有有界normal send queue，不建retry任务；连拒绝都不能入队则终止该Session。 |
| BindOperationTimeout | 10秒 / 10秒 | 从reserve到最终可采用Ack，含所有排队/backpressure；原deadline，超时按W5收敛。 |
| LiveDemandSitesPerClient / WeakCacheKeys | 各64 / 各64 | 同一完整site key canonical弱条目。construction reserve→commit或rollback；只清死weak entry/本地退休/Stop，不驱逐活site并偷偷建重复demand。用户持有多个proxy可以共享同site；不声称限制应用自己保存的对象数量。 |
| TargetsPerSiteSnapshot | 1024 / 1024 | 只含真实Session+token+coherent routing。超过容量拒绝该次扩展/Prepare为Capacity，不能挑“前1024”偷换Picker；旧有效快照可保留，失效移除始终可发布Empty。每site只缓存当前snapshot，不留历史链；旧捕获只由有界inflight工作持有。 |
| PrepareWaitersPerClient | 64 / 64 | Ready同步完成不占；等待前reserve，成功/失败/cancel/finally归还。同site共享Bind，不共享waiter budget。 |
| RefreshWorkPerSite | 1 running + 1 dirty bit | 不每事件排Task；脏标记合并，source generation变化重取快照；stop在现有supervisor publication gate封口并join。扫描每批最多64site，不无限扫dead key。 |
| ProtectedQueuedRequestsPerEndpoint | 65536 / 65536，且受原MaxQueuedCalls/Bytes更紧限制 | 在保留原R进入queue前reserve，queue-only cut通知记录使用同一bounded call/root，不另建无界回调表；真实queue/decoder/cleanup退出后归还。不是提前占business call permit。 |
| StreamStateEntriesPerEndpoint | 65536 / 65536 | 新aggregate界：每个live receive/discard route及outbound credit state各占1；包括terminal仍欠credit/等待peer terminal的状态。先按完整count一次reserve再publish路由；失败全还。仅真实remove/欠债清零后归还，不能在typed child脱离但scalar discard尚live时提前返还。 |
| IdleStreamStatesPerEndpoint | 65536 / 65536 | 不加新pool；现有空send/route state池的aggregate保留上界。回池先清所有异常/collectible/owner引用，再计idle；满则drop空state。复用时从idle移出后按live quota准入，不能复活旧lease/ABA。 |
| P MaxConcurrentStreamsPerConnection | 1024；配置为正Int32，effective≤endpoint剩余65536 | 原per-Session route/stream上界仍同时生效，含discard。新增aggregate只在建/拆state计账，不逐item；其实际启动/收尾成本待N验证，不能宣称无代价。 |
| P send/receive/bytes limits | send queue 8MiB；frame 4MiB（可配1KiB..64MiB）；stream window1MiB、connection16MiB；server pre-admission/retained-compressed/decoded各64MiB | 保留P配置、协商及更紧约束。control frames同样占真实send queue；控制区不绕预算。压缩先核canonical original length及window/quota，再provider；不按压缩小长度借无限原始内存。 |

quota保留的对象只在其owned生命周期可达；服务退休清掉binding payload、facts/Stub/Codec，client retire/Stop清demand/operation/snapshot，无空闲call lease。迟到Release/Invalidate只命中原incarnation；physical尾部不合作时可以阻止容量归还，但容量满后拒绝新工作，不能假归零、分配替代表或放宽hard界。（Q2）

这些新增aggregate账本是必须实现/验证的边界，不是本页已提供实现。#982的回池异常清理和#987的terminal清理顺序保留；不可因为加quota重放已经归还的frame/lease或漏后续cleanup。

## 6. whole-group cut、业务许可与物理保护

### 6.1 发布事务（L1）

注册组是同一次assembly/module publication的全部service R；所有Session上的binding引用同一组gate，不用逐Session close替代group cut。静态R仍受server lifecycle gate/自身RID约束，不能因Module=null跳过token/facts校验；Stop关闭其准入。Bind/Replace/Unregister/local retirement共享其合法cold发布边界：

1. **可失败准备**：加载/验证manifest、依赖、完整facts/hash/Codec ownership、candidate services/immutable snapshots、receipt/RID/generation、quota、invalidations与必要容量全部预备。外部getter、factory、Dispose和metadata callback不在不可逆commit内执行；重入/并发成功winner要求重新验证捕获revision和exact owner。准备失败完整清本次candidate，不破坏旧winner或成功inner publication。
2. **最终重验证**：在cold publication gate确认原R/依赖/registry revision/generation仍是准备输入；冲突重试只重做未提交candidate，失败cleanup不释放别人的owner。不能先关闭旧gate，再运行仍可能失败的Codec/adoption helper。
3. **不可逆commit**：先关闭整个旧组的new-entry/business-start gate，原子选定尚Waiting请求的reject，再发布预建的新registry/Codec/manifest snapshot与receipt；只有nonthrowing host-state转移，无外部callback/新分配。已成功的inner winner不能被outer rollback擦掉。通知/invalidations/旧服务cleanup在解锁后执行；通知失败不重新开旧gate。
4. **物理退休**：对外publication success与old physical release分开。原call/stream/queue/decode/scope真退出才detach R和返回ReferencesReleased；旧immutable结果不自动变true。cancel只停止该caller等待；旧cleanup继续由原endpoint owner跟踪。

#985尚未合并，且等待用户对其readiness前置验收的独立决定。L1必须最终证明冷publication/dependency/unrelated-retirement/failed-outer不破坏winner，但N0不拷入其代码、不取消原失败、不以本合同越过其门禁；当前helpers若仍有fallible/callback commit，未来实现必须在授权范围解决后才可采用。

### 6.2 排队：物理保护不是 grandfathering（L2）

唯一原R owner在exact entry protection成功后贯穿facts、queue/retained head、persistent decode、producer、input/output及scope物理cleanup；selection/envelope/retained payload只能借用，不复制release权。独立business-start claim必须在任何业务payload provider/typed decode、activation、用户interceptor/handler前，与group cut线性化：

| 排序 | 结果 |
|---|---|
| cut先赢，request仍Waiting | 标记Rejected，queue-only通知取消/移出原waiter；R1/R2均不得开始业务，不重新解析、重选或reacquire。已经取得的异步decoder/child/cleanup仍持原保护直到物理退出。 |
| start claim先赢 | 已不再是未准入waiter，允许按原活动call规则执行原R；cut不提前取消它。原caller cancel/deadline、connection failure、grace后forced cancellation仍正常竞争。线程稍后才调度到用户第一条指令，不反转已经赢的claim。 |

采用**主动的原组queue-only通知**，不是C的“所有已获physical保护的queued call都执行R1”。使用同一bounded queue/root与generation-safe call-state lease，必须覆盖cut-vs-enqueue、grant-vs-cancel、晚通知-vs-pooled复用：permit已发但cut赢也要归还；取消Reason沿用原first-terminal/已到期deadline优先级。通知在group/registry/limiter锁外执行；先选定全部相关Waiting状态，再触发可能阻塞的回调。不能提前调用CancelRemainingCalls取消全体活动业务，也不能调用server-scoped admission StopAccepting影响无关组。

P的ModuleCancellation目前是ForcedCancellation，TryBeginDraining并不发queue-only通知；P的TryActivateRequest也不与module cut共享原子边界。因此这个通知/start-claim交接是N真实新增机制，不是把已有token接上就已完成，也不是本页已实现的helper。

**可见迁移**：P waiter只有weak identity、RemainingCalls=0；N的原call/stream lease还保护queued request时计数可非零。cut已拒绝业务/TCS已完成/QueuedCalls已减都不证明physical退出；尚held则ReferencesReleased=false。即使零grace也不保证立即true；原grace/Stop绝对budget不延长。若已拒绝后的scalar error send仍捕获R/CTS，仍要等它或显式清引用并完成安全handoff；不能用“业务不会再用”掩盖框架仍root着ALC。

当前 `QueuedSelectionRetirementShouldPreserveEveryRpcShape` 的14格保留R1/R2 Created=0和不retarget，但迁移module-count/零grace释放的观察阶段，**不改成C的R1 Created=1**。`QueuedSelectionShouldNotRootRetiredCollectibleService` 不再要求同一个仍持原强保护的queue item同时可卸载；改为cut撤销该旧waiter、物理清理/引用清空后，在仍活动endpoint和原GC/时间预算内证明ALC回收。held decoder期间先证明不能提前释放。5格queue cancellation保留cause、无业务、permit/queue归零，另补cut/grant/late-callback竞争，不增加timeout/GC或关闭endpoint求绿。（L3）

### 6.3 五族 owner 与 guard（L4）

| 边界 | 冻结顺序/保留的差异 |
|---|---|
| static Unary | Codec null → scoped identity/shape → caller cancellation →一次interceptor/policy/control capture→valid bound target→serialization。 |
| static OneWay | request Codec null→identity/shape→caller cancellation/一次capture；不自动继承client默认timeout；head本地发布后typed writer可开始，无逐请求Ack/业务确认。 |
| static ClientStreaming | request/response Codec null→identity/shape→caller cancellation/一次capture；RequestAccepted前零producer；Ack后还须在原pending generation终态门争start，晚Ack不复活。 |
| ServerStreaming / Duplex | 调用时捕获interceptor/policy/deadline（含原method-timeout验证/parent deadline）；static Resolved再作Codec-null→scoped identity/shape→原logical progress。动态首次枚举先progress，再acquire原local module，再Resolved Codec/identity/progress；不移到factory或重新采样budget。caller-token仍按P的deferred观察边界，不在调用时新增统一ThrowIfCancellationRequested。Duplex的consumer、head和producer分别持原物理保护。 |
| dynamic Unary/OneWay/ClientStreaming wrapper | 原module acquire先于inner Codec/cancel；ClientStreaming保留独立call/producer保护。不能为统一guard改变错误时机。 |
| server | framing→full token exact entry→same-entry protection→complete facts/shape→resource/business admission→payload/provider/activation/auth/interceptor→Stub。RequestAccepted仅在route/resource及现有授权/interceptor terminal已允许后、generated Stub前；不能将动态授权冻结在Bind。 |

原R/facts/Codec与原call-state CTS、pump linked CTS必须保持到下列工作真的结束：queued head、persistent decode、input mailbox child/dispatch/disposal、client producer/writer、output MoveNext/send/唯一Dispose、ServiceLease/scope cleanup。逻辑response/cancel/deadline只选终态，不代替退出。复用P已合入的边界：

- #984：invoke前capture稳定input drain；正常返回和同步throw catch都走同一物理join，不能只在core正常返回后套wrapper。先无外部回调关闭全部input业务准入，再清理/join已取得typed child；稳定scalar discard/credit route可留到peer terminal，不再保留旧业务Codec。
- #988：existing output wrapper/returned work先join未完成MoveNext/send，再唯一Dispose（含compiler iterator），最后linked CTS/外层owner；EOF已选deadline时也先cold handoff再调用可能同步阻塞的Dispose。晚结果不再serialize，晚fault不能跳过Dispose或覆盖已选logical cause。
- #987：frame/observer cleanup逐项推进，不重放已退slot，不漏后续owner；canonical transport cause、原CTS/异常身份保留。失败cleanup不能阻止其余scope/lease释放。

不能靠等待不合作用户代码退出才给caller逻辑deadline，也不能新增server wire DeadlineExceeded承诺。同一原Session须能处理第二个合法RPC；Stop保留原绝对grace+cleanup budget，未物理退时报告未释放并由原owner继续持有，不把连接读循环await所有tail卡死。Connection/Call/Singleton都适用，不只测同步Unary。

## 7. 移除、迁移与 #805 的精确范围

本表冻结的是**声明/消费者 delta**，不是伪造最终Roslyn成员总数。新required成员、constructors、record生成成员和enum/constants均须在实际N库存中展开；本页不更新现有`eng/public-api`批准快照或旧302/3397台账。（M1）

| 操作 | 精确声明/落点 | 所有实际消费者及验收 |
|---|---|---|
| 移除 | ISharpLinkClient.Get/GetWithMetadata、ISharpLinkMultiClusterClient同二方法；IRpcChannel全类型/五Invoke/RuntimeContext；SharpLinkClient的IRpcChannel实现和client-wide raw forwarding | `ClientProxyLookup`/Client factory、MultiCluster route、Hosting/accessor、demo、samples、GenerationControl使用点、Load/StreamLoad/Benchmark、所有Unit/Integration/manual raw probes。改为CreateProxy(...).Service，需显式准备者保留handle；metadata移options；手写调用GetLocalContract→CreateContractChannel。旧raw casts必须精确编译失败，不留Obsolete alias/default identity。 |
| 新增 | IRpcContractChannel、RpcLocalContract、RpcProxyOptions、RpcRegistrationRequirement、RpcRegistrationReceipt、RpcProxy<T>；ISharpLinkClient RuntimeContext/两GetLocalContract/CreateContractChannel/CreateProxy；MultiCluster.CreateProxy | Generator.ProxyEmitter/ManifestEmitter、module/metadata wrappers、typed/manual五族、动态local代次、foreign/retired/初次连接与same-site Prepare。RuntimeContext从client/channel访问，不internal化standalone serialization。 |
| 新增 | ISharpLinkServer三receipt方法、两个sealed publication-result类型及其properties/internal ctors | 服务动态发布、GenerationControl等控制caller、外部custom ISharpLinkServer的NotSupported正例；同次commit多service receipts、契约/服务分离owner、取消后不能查询latest充当原结果；纯scalar不rootALC。 |
| 修改 | RpcMethodDescriptor加SupportsCancellation；required IRpcStub resolver/四Invoke；IRpcGeneratedServerBridge输出签名；generated method descriptor第八Facts；contract ProxyFactory delegate | 所有generated/custom Stub、manifest、codec-provider factories、测试fake/外部SPI、ExceptionMapper/telemetry/admission/queued/decode。resolver在Bind后设置成throw的真实控制须仍可完成所有合法路径；所有facts字段/nullable/fullticks一致。 |
| 修改 | GeneratedManifestVersions Api/AbiIdentity；locator/bootstrap精确preflight；ProtocolV2 minor/floor/6个frame enum值；HandshakeResponse两个required Guid、对应codec；ContractManifest身份 | 错旧factory/API5/C-API6/profile/locator在constructor前拒绝；旧wire4/5/36B在业务前拒绝。ProtocolV2仍public数据/解析SPI，不误称live Session mutator。 |
| 新增 | SharpLinkErrorDetails.ContractBinding嵌套public static类及W6列出的14个const；ResourceExhausted.StreamRouteCapacity=15 | error-code scope/round-trip/truncation、未知detail保留；不匹配Message，不修改现有1..14或异常继承开放性。 |
| 保持 | 既有RuntimeContext/Builder、standalone generated serialization、owner-bound Codec、transport/SPI、合法继承和SDK既有forwarders | #805已定keep边界保留；不恢复AddCodec/UseCodecResolver，不因编译失败暴露internal Session/token setter，不误删外部transport/DI/反射能力。 |

迁移清单由实际源semantic references完成，不把lexical文件数当oracle。必须覆盖`src/SharpLink.{Abstractions,Client,Server,Runtime,Generator,Sdk,Hosting,GenerationControl}`、generated/handwritten contract factories、`demo/`、`samples/`、所有测试项目（含实际Package/AOT/Trim/ABI消费者）、bench工具、`eng/`脚本/fixtures、XML/migration/QuickStart/ProductionTemplate。当前获取proxy、metadata与raw RuntimeContext的每个原入口都要有对应新用法；没有消费者的独立新平台不算检查点成果。

**#805 gate顺序（M2）**：

1. 对新增/改/删、继承/可见性、required SPI与精确guard先做独立增量review；source/provenance绑定N，不重做已通过且未变的库存工具。
2. 最终N从实际reference/implementation assemblies与nupkg提取API/forwarder/grants，与真实2.0.3、已审3.0 baseline及本页批准delta对照，形成新的baseline；3.0允许显式breaking，不要求伪装2.x二进制兼容。
3. 外部typed/manual五族、metadata/RuntimeContext、custom Stub/manifest、Hosting/DI/反射/transport/继承正例；旧raw/缺required resolver+四签名/错误ABI locator精确负例。#973/#974可复用结构，必须由N实际包执行。
4. pack、QuickStart/ProductionTemplate、SDK forwarding、动态模块/活跃endpoint ALC、Trim full/partial、Linux/Windows/macOS NativeAOT按**实际支持能力**验收。公开NativeAOT RegisterAssembly仍不支持；内部模块harness不能冒充支持，新能力须另作决定。

## 8. 每个决定的 oracle 与后续接受门

以下标识是后续验证合同，不是声称已有同名测试或已执行的新UID。实施前须从真实discovery冻结新增/迁移的精确UID、原预算和停止条件；不能抄历史2212/502计数。P当前普通Unit2233、Integration discovery524仅作来源值，N另取实际库存。

| Oracle | 必须观察的事实 / 改变的旧含义 |
|---|---|
| A1 factory | 连接前同步返回，foreign/retired/ambiguous精确失败；mutation options/metadata后site不变；失败无demand泄漏。Get旧“返回Service”变成Service+same-site handle，不以Manifest Ready冒充Bind。 |
| A2 Prepare | ready sync不新Bind；invalid timeout/cancel/lifecycle按序；并发waiters合并一个operation而budget独立；reconnect/earlyAck/取消不重置有限deadline。 |
| A3 receipt | stale Manifest、同S/epoch的指定R可Bind；wrong S/epoch/hash/RID拒绝；无关global generation不得满足Minimum；同次publication receipt不随之后Replace变化。 |
| F1–F4 facts/ABI | 32B无引用/fullticks/所有flags；unknown不造fallback；Bind后resolver throwing，queue/mapper/telemetry/五族Stub仍用原facts；实际包的required-member/locator负例精确。 |
| W1–W4 wire | major/header、minor/floor6、固定长度/flags/count/压缩区、Handshake→Manifest identity一致；真实旧wire16B/36B/C5/profile peer因version/magic边界被拒，相关请求业务Codec与激活均0，不凭字节意图猜格式。相同数字token跨Session不能搬用A的绑定，在B只可能代表B自己独立安装的entry。 |
| W5–W6 control | Ack-before-enqueue不得Ready；late/duplicate/invalidate不复活，full token ABA/operation回绕耗尽；quota压力/backpressure/timeout/Session关闭保留原writer并最终归还，无后台无界重试。 |
| Q1–Q2 bounds | 每个限额的boundary与+1拒绝、reservation失败rollback、旧Release不还新quota、正常/取消/异常/断线/ALC无残留。小window/压缩恶意original length在provider前受界，discard至peer terminal不冒充physical owner。新增aggregate启动/收尾成本要计入N。 |
| L1 publication | fallible准备/reentrant getter/competing winner、group多service/多Session cut；失败outer不破坏inner，unrelated/removed/surviving Codec provider控制。不得以N0越过#985的用户决定。 |
| L2–L3 queue | cut先赢：R1/R2业务0；claim先赢：合法原R活动调用。held queue/decode/回调期间module/CTS不早退；permit-grant竞争归还一次；ReferencesReleased与ALC改在真实退出后观察，不搬C的grandfathering。 |
| L4 physical/guard | 原typed Codec/service/facts/CTS身份跨delay/queue/async业务/provider/producer/MoveNext/send/唯一Dispose/scope。同步throw/async fault/取消/cleanup失败均不漏尾部；五族原wrapper guard/time捕获差异、无Ack OneWay、同Session第二RPC、Stop绝对budget与活跃endpoint ALC。 |
| M1–M2 migration | 无Get/raw/default facade残留，所有caller/实际包/SDK/外部SPI/Trim/AOT完成；source与包库存、负例及批准delta一致，不靠编辑approved snapshot吞新缺口。 |

### 8.1 证据复用与停止条件

- S7 `run43569926080` 的四格99+67+49+49+64+76+84、facts/stream/local历次反例，以及C的普通包/transport报告可作为**case、算法和原guard来源**；N改了产品/ABI/wire/queue语义，不能把这些旧绿色直接标成N资格。
- P #984/#987/#988的实际source、合法controlled gates与未变检查可复用。历史R3四输出反例、Q1 fixture失败、独立EOF反例、Q2改变前提验证保留原标签；不把旧失败回写成新绿色。
- C/D的输出detach、同步OneWay throw源级反证与D HOLD保留；P修复不跨commit转绿。旧40B真实分配观测保留；原Native A/A时序无效不抹掉allocation，也不能当N回归/收益。
- N任一错身份/guard顺序、premature Dispose/RefsReleased、quota缺口、隐藏mapper错误、缺UID/skip/timeout、未回收ALC、actual package不一致，均停止相应采用资格；修具体因果后新subject只补改变前提，不rerun刷绿、延长预算/GC或关endpoint掩盖。

### 8.2 后续性能门槛——本阶段不执行

先完成上述真实五族/owner资格及N实际layout/newobj/async-frame审查，再另获授权冻结P/N同机、同runtime、同语义边界、有效A/A的成本计划。普通稳态目标是替换原路由工作、不新增稳定per-call allocation；既有研究的额外16B/op上界不是自动批准新常驻分配，40B也没有豁免。任何拟接受新成本须显式记录，不能借无效时序A/A或遥测局部改善抵消。

冷factory/Bind/短流/组退休/维护、动态与静态、五族、真实transport及NativeAOT能力分别列输入；内部同步Unary仅能回答对应owner/site成本，不替代公开RPC吞吐/尾延迟。固定每输出启动cast/cleanup branch、stream-state aggregate计账等实际变化必须进入适用控制，不宣称计时等价。现有allocation CI不等于完整#775性能验收。

最终canonical/Unit/Generator/Integration/真实transport、竞态/ALC、包/Trim/AOT/API/ABI/wire与有效性能均须在同一最终采用HEAD满足原合同，才能合并原子采用包并讨论#775/#805关闭。#387/SHM暂停调查不在N0恢复；若最终接受依赖其处置，由用户另行决定，不skip或借文档改门禁。

## 来源索引

- [#775 实时主目标/原始问题/已合窄修](https://github.com/SunSi12138/SharpLink/issues/775)、[#805 保留能力与最终实包签审](https://github.com/SunSi12138/SharpLink/issues/805)、[2026-11-01物理义务移交](https://github.com/SunSi12138/SharpLink/issues/775#issuecomment-6551137275)。
- P直接合同：[公开RPC语义](public-rpc-semantics.md)、[发送/流](calls-and-streaming.md)、[动态模块](dynamic-modules-and-multicluster.md)、[接入控制](admission-control.md)、[错误details](error-details.md)、[#984输入](dynamic-oneway-input-drain-validation.md)、[#988输出](output-stream-physical-lifetime.md)、[#805保留台账](3.0-api-surface-audit-decisions.md)。这些仍描述P；本页不让新签名冒充已实现产品。
- S7/C/D均以本页第1节固定commit读取；历史profile魔数/限额、C API6/minor5与D布局不授予当前采用资格。N0取代的具体选择是C grandfathering、C精确ABI字符串/minor5，以及实验总quota直接当生产默认的做法；其原始正负证据不撤回。
