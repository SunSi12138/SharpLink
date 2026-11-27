# 在现有连接上协调服务换代

如果要把动态服务程序集从 R1 换成 R2，并让已准备的客户端代理在原连接上继续调用，使用 `ReplaceAssemblyCoordinatedAsync` 和 `JoinVersionChangeAsync`。例如，R2 只更换计价实现，契约与序列化保持不变：已获准开始执行的调用在 R1 完成，恢复路由后的新调用进入 R2。换代不保证每一刻都能接受新调用。

这是 SharpLink 3.0 Core 的能力。客户端须已安装兼容的动态契约，服务端须在本地取得 R2 程序集。它不会下载或安装对端程序集、迁移跨版本 wire 契约，或把固定代理搬到另一条连接。R2 必须保留整个注册组的契约集合、服务端契约类型和程序集哈希。NativeAOT 可以查询预编译的静态库存，但不能用这套流程加载未知 DLL；部署新进程并排空旧进程。

## 在握手前启用协调

在 Server 开始接受连接、Client 开始连接之前，分别调用 `SetVersionCoordinationMode`。默认为 `Disabled`。`Automatic` 不等于自动加入：两端应用仍须显式参加同一次换代。人工审批用 `Manual`，在下列两个调用中都传入 `approveLocally`，按本端政策返回 `Approve`、`Reject` 或 `Defer`；回调须响应取消。改模式不会给已有连接补充握手能力。

## 让两端加入同一次换代

以下是只有一个相关客户端连接、由服务端发起的 Automatic 例子。`IPricing` 是应用已安装的动态契约；`oldAssembly` 是已注册的 R1，`newAssembly` 是应用加载的 R2。两个代码块在各自进程中由同一个发布操作启动，不要先等待其中一端执行完才启动另一端。`cancellationToken` 是应用对此次操作的取消令牌。

客户端先准备代理，再加入。可从库存回执取得此时的物理连接选择器：

```csharp
var proxy = client.CreateProxy<IPricing>();
await proxy.PrepareAsync(TimeSpan.FromSeconds(5), cancellationToken);

var peer = await client.QueryPeerVersionInventoryAsync(cancellationToken);
var session = peer.Session ?? throw new InvalidOperationException("没有可用的物理连接。");
var participant = new SharpLinkVersionParticipant(session, SharpLinkVersionInitiator.Server);
var joined = await client.JoinVersionChangeAsync(
    client.GetLocalContract<IPricing>(), participant, cancellationToken: cancellationToken);
// joined.Resumed 为 true 后，原 proxy.Service 的新调用才会进入新路由。
```

服务端用本端看到的同一物理连接启动替换。从目标契约的生成 Manifest 描述符取得 `ContractId`；它指向本次要恢复的动态契约，实际替换的是整个注册组：

```csharp
var session = server.GetVersionPeers().Single(); // 此示例只有一条 Ready 连接。
var participant = new SharpLinkVersionParticipant(session, SharpLinkVersionInitiator.Server);
var changed = await server.ReplaceAssemblyCoordinatedAsync(
    oldAssembly, newAssembly, contractId, [participant], TimeSpan.FromSeconds(5),
    cancellationToken: cancellationToken);
```

恰有一条 Ready 连接时，固定客户端和集群客户端都可以直接查询库存或声明目标。有多条 Ready 连接时，通过 `GetVersionConnections()` 选中当前会话，再使用带选择器的版本查询或声明；重连后重新选择。服务端参与者列表必须包含所有仍对该组任一服务保有打开绑定、且支持协调的连接，包括只绑定同组其他契约的连接。首个参与者决定服务端能否发布；其余参与者各自决定能否恢复，不能否决已经发布的 R2。

已激活的连接可以在原连接上为同组其他契约重新准备。R2 已发布但跟随连接未获激活时（例如跟随者拒绝、暂缓，或没有目标契约的源绑定），整组 R2 在该物理连接上仍不可准备；独立注册组仍可调用。若应用随后决定使用 R2，先取得新物理连接，再准备该组的代理。

同一连接正在为另一契约准备或释放绑定时，换代可能在冻结旧路由前返回 `SourceUnavailable`。已有路由不因此断开，等正在进行的操作结束后可以重新发起尝试。已经开始激活时，未准备的独立契约可能短暂收到 `ResourceExhausted`；等激活结束后重试 `PrepareAsync`。已有的独立路由仍可调用。

## 分开判断发布、恢复和排空

| 要确认的状态 | 看哪里 |
|---|---|
| 服务端是否发布了 R2 | `changed.Replacement.Succeeded`；没有返回结果时查服务端 `GetLocalVersionInventory()`。同一个 `AssemblyHash` 仍可能对应不同 `RegistrationId`。 |
| 指定客户端是否可以继续调用 | 客户端 `joined.Resumed`。服务端 `changed.Peers[i].ActivationSent` 只表示发出了激活，不是客户端已恢复的回执。 |
| R1 的框架引用是否已释放 | `changed.Replacement.ReferencesReleased` 和 `RemainingCalls` / `RemainingStreams`。到排空时限仍未释放，框架会继续清理；应用还须释放自己的引用才能卸载 ALC。 |

`Defer` 会结束当前尝试；下次需要重新审批。遇到超时、取消、断连或观测中断，按[先核对清单、再恢复或重试](rpc-channel-3.0-migration.md#换代中断后先查清单再重试)处理。发布一旦提交，不因等待被取消而回滚。`ObserveVersionChanges` 可用于观测，但通知可能丢失，`Completed` 也是本端状态；不要用它代替库存与客户端恢复结果。

如果只需要交换应用自定义的部署清单，而非恢复某条已准备的 Core 路由，请用可选的 [GenerationControl](generation-control.md) 方案。
