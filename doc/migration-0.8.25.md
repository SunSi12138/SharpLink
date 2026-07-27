# SharpLink 0.8.25 迁移指南

English: [`en/migration-0.8.25.md`](en/migration-0.8.25.md)

0.8.25 不改变 Protocol v2、route hash、payload layout、Manifest schema 或 top-level contract 的生成 Proxy/Stub type name。Roslyn hint name 仅是 build-internal 标识，现在追加稳定 contract ID，不影响调用方源码。

公开 nested contract 现在获得包含 containing-type identity 的唯一生成 peer name。若代码直接引用旧的 nested `IInner_Proxy`/`IInner_Stub` 名称，请改用 Client/Server 的 contract API，或按生成源码中的新名称调整；正常 `Get<TContract>()` 与 Manifest 注册无需改变。contract 及每层 containing type 必须 public，否则报告 `SHARPLINK055`；泛型 containing type 报告 `SHARPLINK005`。

合法的 C# keyword method/parameter 现可正常生成。`ref/out/in` 与 by-ref return 报告 `SHARPLINK052`，static method 报告 `SHARPLINK053`，abstract property/indexer/event 报告 `SHARPLINK054`。这些表面此前无法得到可编译或完整的代理，不存在可保留的 wire contract。带实现的 default interface member 仍可作为本地 helper 存在，但不会成为 RPC route。
