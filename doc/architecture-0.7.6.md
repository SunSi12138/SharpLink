# SharpLink 0.7.6 动态端点与 Resolver 设计

本文档描述 0.7.6 在 0.7.5 静态 endpoint cluster 之上增加的动态拓扑能力。它不改变 Protocol v2，不新增握手 capability，也不改变固定单端点或静态 cluster 的调用路径。

## 启用方式与边界

动态模式必须显式配置，且与 `UseTransport`、`UseEndpoint`、`UseEndpoints` 互斥：

```csharp
var client = SharpClientBuilder.Create()
    .UseEndpointResolver(resolver, SharpLinkTransportFactories.Sockets())
    .UseCluster(options => options.MaxConnections = 4)
    .Build();
```

`ISharpLinkEndpointResolver` 位于 `SharpLink.Abstractions`；它只返回完整的 `SharpLinkEndpointSnapshot`，不创建或持有 transport factory。Client 拥有 resolver，并在 Stop/Dispose 中恰好调用一次 `DisposeAsync`。静态与固定模式不会创建 resolver worker、候选数组或额外调用路径。

`SharpLinkEndpointSnapshot.Version` 必须严格递增。Client 对 `version <= lastAcceptedVersion` 的快照直接忽略；新快照先完整复制、冻结并校验，再做 reconciliation。重复 ID、非法地址、超出 `MaxEndpoints` 或属性限制会使整份快照被拒绝，最后一个成功拓扑继续服务。空快照合法，它会移除全部新调用候选，但仍允许 `WaitForReady` 等待后续恢复。

## Resolver 生命周期

`ConnectAsync` 先调用一次 `ResolveAsync`，接受初始拓扑后启动一个有界 watch worker。Worker 正常结束、抛出异常或 Resolve 失败时保留 last-good topology，并按 100 ms 起步、最大 30 s、±20% jitter 的退避重试。任一次成功 Resolve 或 Watch 更新都会复位退避。

Stop 先取消 Client 生命周期 token；watch、resolver retry 和 endpoint reconnect 都以该 token 为退出条件。Stop 获胜后不再接受快照或创建连接，随后等待 worker、释放 connection/factory 并 dispose resolver。`DelegateSharpLinkEndpointResolver` 可桥接任意 Consul、Nacos 或 Etcd SDK：提供 watch delegate 时直接转发；没有 watch 时只以一个有界 polling delay 调用 resolve delegate。

## 拓扑协调与 generation

动态 runtime 维护一个单 writer 的 current topology 和独立的 retired generation 集合：

- 新 ID：创建新的、Client 所有的 factory 与单调递增 generation。
- 相同 ID 且 `Address + Authority` 相同：复用连接和 factory，仅替换冻结后的 Attributes。
- 相同 ID 但地址或 Authority 改变：先发布新 generation，再使旧 generation 退出候选并进入 draining。
- 删除 ID：在同一次原子发布中从候选移除，已有调用与 stream 继续绑定旧 connection 至完成。

Ready endpoint/candidate 对以一次 `Volatile.Write` 发布。属性更新也强制重建控制面 candidate snapshot，使自定义 selector 立刻读取新的属性；连接数、active calls 和选择过程仍不获取 topology writer lock。稳定调用不因 resolver 无更新而分配 topology collection。

Retiring connection 不计入 active Ready/Connecting budget。超过 `MaxRetiringConnections` 时，拓扑仍然接受更新，但抑制新的 replacement connection，而不会强杀用户 stream；当 draining connection 和 active call 归零后，旧 factory 恰好释放一次。

## 内置 DNS Discovery

`UseDnsEndpoints(host, port, factory, configure)` 使用 `SharpLinkDnsEndpointResolver`。它查询 A/AAAA（可按 `AddressFamily` 过滤），对规范化 IP 去重排序，并由 host、port、address family 和 IP 构造稳定 endpoint ID；原始 host 保留为 Authority，因此 TLS 默认 SNI/证书主机名不随 IP 变化。地址排列变化不会发布 snapshot；地址增加/消失分别表现为 add/remove。

BCL 无法提供可移植的 DNS TTL，因此 resolver 使用 `RefreshInterval`（由最小/最大 interval 约束）与可选 jitter，不伪造 TTL。查询失败保留 last-good 结果。DNS 查询器在内部可替换，测试不依赖公网 DNS；一个 resolver 只运行一个 refresh loop，不创建每 endpoint timer。

## 验证范围

0.7.6 的 Unit/Integration/PackageSmoke 覆盖 DNS 规范化和 last-good、resolver 所有权、初始空拓扑恢复、add/remove、同 ID 地址 generation 替换、Attributes-only 更新、正常 Watch 结束、resolver failure/retry、流排空和新调用迁移。所有行为都是客户端本地行为，继续与 0.7.4 Server 互操作，且保持 NativeAOT 可用。
