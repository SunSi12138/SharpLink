# Phase 17 — vNext Generated ABI 冻结与旧 artifact 启动期 fail fast

本阶段把 2.0 的进程内 Generated ABI 原子提升为 **API 5** 并冻结，旧生成程序集在
registration/startup 边界被明确拒绝。Protocol v2 wire format、Contract/Method/DTO ID 与
schema 规则不变。

## 为什么是 API 5 而不是继续 API 4

`dev` 在 commit `b1aebf2`（"feat: cut generated server ABI to API 4"）完成过一次原子 ABI
切分。该冻结点之后、本阶段之前，合入的 #167（contract-owned Codec 架构）与 generated
sized-codec 性能工作改变了 generated-visible 表面：

- 生成 DTO Codec 新增实现 `IRpcSizedCodec<T>`（`IRpcSizedCodecSnapshot` /
  `RpcGeneratedCodecSizing` 同步加入 Abstractions）；
- 生成 string/collection Codec 引用新的 `RpcGeneratedCodecWire.MaximumStringPayloadBytes`；
- Codec factory 进入 adapter-free 语义并携带 schema identity
  （`RpcCodecAttribute` / `RpcCodecImplementationAttribute`）。

老 API 4 程序集（未实现 sized-codec 面）仍能通过 `is` cast 回退路径运行，因此这不是二进制
加载级 break；但 `Api = 4` 已无法区分冻结点前后两种能力面。按 issue 执行手册的"情况 B"，
本阶段把 post-#167 表面冻结为 API 5：current artifact 声明 5，previous self-describing
artifact（4）与 legacy locator（3）都在实例化 manifest 之前被拒绝。

## 版本门禁

- `SharpLinkGeneratedManifestVersions.Api = 5`，`Protocol = 2` 不变；两轴独立，Generated
  ABI 不进入 wire handshake。
- 中央校验：`SharpLinkGeneratedManifestCompatibility.Validate`（先版本、再 shape、后
  ownership），所有普通 build/registration 入口经由此路径。
- 动态加载：`SharpLinkDynamicModule` 在 `Activator.CreateInstance` 之前读取
  `CustomAttributeData`：legacy 单参数 locator → API 3 早拒；自描述 locator 元数据不匹配 →
  `IncompatibleManifest`（expected/actual + regenerate 行动说明）；materialize 后再由中央
  校验 shape/ownership。
- 版本校验只发生在 load/registration/startup 边界；调用、proxy、stream item 热路径不比较
  ApiVersion，也没有 per-call adapter。

## 版本矩阵

| 场景 | 结果 |
|---|---|
| current ABI（5）+ Protocol 2 | success |
| API 3 legacy locator（1.1.x binary fixture） | early incompatible |
| API 4 previous self-describing（冻结 binary fixture） | early incompatible |
| future ABI metadata（6+） | early incompatible |
| wrong Protocol | incompatible（先于 shape 读取） |
| locator 元数据 ≠ materialized manifest | InvalidManifest |
| missing locator | MissingManifest |
| malformed locator | InvalidManifest |
| current 元数据 + malformed manifest | semantic error |

- 冻结 fixture：`test/fixtures/generated-api3`（1.1.x 发布包构建）与
  `test/fixtures/generated-api4`（bump 前 dev 树内 generator 构建，provenance 记录 commit
  与 SHA-256）。二者都不是"current 接口返回旧数字"的假 fixture。
- 入口覆盖：direct loader、Client/Server/multi-cluster 的 registration 与 replacement、
  collectible ALC 释放、无 snapshot 发布，见 `Api3BinaryFixtureIntegrationTests` 与
  `Api4BinaryFixtureIntegrationTests`。
- 能加载进入 SharpLink 边界的旧程序集必须早拒；若 CLR 因真正缺失的 binary member 无法加载
  旧 DLL，属 pre-runtime binary load limitation，不在 Runtime 可保证范围（见 issue 执行手册）。

## 验证

- `dotnet build Sharplink.slnx -c Release`
- `dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj -c Release`
- `dotnet test --project test/SharpLink.Generator.Tests/SharpLink.Generator.Tests.csproj -c Release`
- `dotnet run --project test/SharpLink.IntegrationTests/SharpLink.IntegrationTests.csproj -c Release -- --timeout 120s`
- NativeAOT smoke（由当前 generator 重新生成）；Protocol v2 golden/contract ID 不因版本号
  bump 变化。
