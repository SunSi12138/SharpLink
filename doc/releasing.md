# 发布流程

本文定义 SharpLink `2.0` 及后续版本的正式发布门禁。发布对象必须是一个已提交、工作区干净且可由标签唯一定位的精确提交；RC 性能数字不能来自标签前后的近似版本。

## 版本与兼容性

- NuGet 包版本由 `VersionPrefix` 和可选的 `VersionSuffix` 组成。稳定版不设置后缀；预发布版本可使用例如 `rc7` 的后缀。
- `AssemblyVersion` 与 `FileVersion` 始终使用四段纯数字；预发布后缀只进入包版本和 `InformationalVersion`。
- 冻结前更新 `CHANGELOG.md`、包引用示例和迁移说明。公开 API、Protocol v2、生成代码、契约 Manifest 或默认行为的变化必须明确标注兼容性。
- `2.0.0` 发布后保留其公开 API 包作为后续 `PackageValidationBaselineVersion`；不在补丁版本中进行破坏性 API 或 wire 变更。

## 本地冻结门禁

在干净工作区记录分支、完整提交 SHA、SDK/runtime、OS、架构和 CPU，然后依次完成：

1. 强制还原并执行非增量 Release 构建，要求零警告、零错误。
2. 执行 Generator、Unit、Integration 全套测试；Integration 必须覆盖真实传输、TLS/mTLS、认证授权、取消、deadline、流式背压、接入控制、优雅排空和故障恢复。
3. 打包全部九个 NuGet 包并确认：版本一致、依赖版本正确、仓库提交正确、主程序集具有 XML 文档、符号包具有 portable PDB、SDK 包具有 Generator。
4. 使用空 NuGet 缓存执行 `SharpLink.PackageSmoke`，避免项目引用或开发机缓存掩盖缺包。
5. 在支持的平台执行独立进程 SharedMemory NativeAOT smoke；其余平台由 Release Gate 矩阵完成。
6. 在 GitHub Actions 上启动 10 分钟 release soak（TCP / SharedMemory 并行）；本地不运行长稳。2026-10-03 发布负责人明确要求约 10 分钟：release soak 默认由 5 小时改为 10 分钟，同次发布验收的 Generated ABI Dynamic-module Churn 由 2 小时改为 10 分钟；日常定时 ABI 长稳仍为 2 小时。release soak 检查点由每 30 分钟改为每 2 分钟，以覆盖缩短后的窗口；并发、重启间隔、故障注入、恢复与资源归零检查保持。最终报告必须记录实际时长与精确 SHA；ABI 每轮完整执行，末轮可能使实际时长超过 10 分钟，额外时间取决于完整末轮。缩短观察窗口不代表已验证 5 小时稳定性。发布前等待当前必需长稳正常结束并通过，已经观察到的非注入错误、崩溃、恢复超时或资源泄漏仍须先处理。2.0.0 当时获批的 5 小时异步验收例外只属于该历史版本，不自动沿用到后续发布。
7. 在同一精确提交执行下述 [3.0 完整性能验证](#30-完整性能验证)，保存原始 JSON 和环境快照，只把可复现汇总写入仓库。[历史成绩页](performance.md) 不能代替当前候选的验证。
8. 执行传递依赖漏洞和弃用扫描；高危漏洞或运行时可达的中危漏洞必须在发布前解决。

### 3.0 完整性能验证

实际执行入口是 [`eng/run-performance-matrix.sh`](../eng/run-performance-matrix.sh)。其默认 tier 为 `smoke`、默认 runtime 为 `jit`，不能省略配置后把结果称为完整 JIT/NativeAOT 矩阵。完整候选在支持的平台使用：

```bash
performance_root="$PWD/artifacts/performance/3.0/$(git rev-parse HEAD)"
mkdir -p "$performance_root"
performance_attempt="$(mktemp -d "$performance_root/attempt.XXXXXX")"
SHARPLINK_MATRIX_TIER=full \
SHARPLINK_MATRIX_RUNTIMES=jit,aot \
SHARPLINK_MATRIX_REPETITIONS=5 \
SHARPLINK_MATRIX_OUTPUT="$performance_attempt" \
bash eng/run-performance-matrix.sh
```

每次尝试均创建新的 attempt 目录，分片也使用各自独立的新目录。脚本会删除同名旧报告并复用 AOT 输出，不能通过复用旧路径补跑来覆盖先前失败或样本。

`full` 当前包含五种传输（Windows 默认排除 UDS）、三种 profile、六种 Unary payload、五轮重复及 pool 1/1、1/4（AnonymousPipe 仅 1/1）。默认并发为 1/8/32/128/256/512，64 KiB payload 降至 1/8/32/128、1 MiB 降至 1/8/32；固定队列 OneWay 为 c1，多生产者背压单列且仅在 JIT 执行。另有 yield/delay 和 `streams --operation all --stream-size 256`。预热 5 秒、测量 20 秒；以脚本的实际参数和报告内容为准，报告文件数不等于测试 cell 数。

可按传输、profile、runtime、workload 分片执行，但须在运行前记录完整预期覆盖表，保留每个分片的配置和失败结果，并核对合并后的缺失、重复、跳过及平台限制。降低重复数、缩小 payload/并发或只运行 smoke 不能作为完整矩阵交付。[Performance Matrix Smoke 工作流](../.github/workflows/performance-evidence.yml) 是正确性导向的短验证，不是正式完整性能验收。

每份证据绑定干净 source SHA、实际 DLL/NativeAOT 可执行文件哈希、SDK/runtime、OS/架构/CPU及全部 workload 参数。版本对照应预先固定 baseline/candidate 和判据，在相同机器、配置与负载下交替执行，保留全部样本；环境或工具版本不匹配的实验不得重标成发布证据。现有 allocation、采样准确性与扰动门槛独立保留，不因完整矩阵运行成功而视作已通过；统计上未发现显著差异也不等于已证明等价。

OneWay 矩阵记录的是有界本地发送接受，不是接收端业务完成；需要接收端吞吐结论时必须另有业务完成计数和排空证据。#732 方法事实、#775 冷维护等具体改动还需各自的生命周期、生成代码尺寸和匹配性能证据，不能用通用矩阵替代。下述 2.0.3 有限例外不适用于 3.0；本节描述执行与证据要求，不宣称当前 3.0 候选已通过。

### 仅限 2.0.3 的性能验收例外

2026-10-04，发布负责人在了解原采样干扰门禁失败及尚未证实的根因后，明确接受本次有限性能验收并要求继续发布工作。此例外只适用于 `2.0.3`，不改变后续版本的默认完整矩阵或采样器原分析器。

- 在最终干净 `main` 的精确提交重建并核对实际 DLL 身份，执行原 micro 1M × 5 的全部 100 点、原 42 进程交替干扰对照和 Dual 准确性检查。原分析器、原阈值与所有失败值完整保留；仅 P99.9 的绝对 ±3% 失败作为本次接受并披露的性能局限，不再单独阻止发布。吞吐/P99 的绝对 ±3%、分配 ≤0.001 B/record、零错误、准确性、样本完整和排空仍是硬条件。
- 执行 36 个单轮正式采样探针：TCP/SharedMemory × Balanced/LowLatency/Throughput × Add/Echo32B/OneWay × c128/c512，池 1/1、发送队列 64 MiB、5 秒预热、10 秒测量、容量 300M；保留正常 OneWay 有界重试语义。零错误、完整样本、资源排空、精确提交及无超时必须通过，不把单轮结果称为完整五轮基线。
- 有限性能部分预计约 20–25 分钟，总预算 30 分钟；超时无效，不自动重试。省略 UDS/NamedPipe/AnonymousPipe、池 1/4、更大 payload、完整矩阵五轮、Async/Stream 性能、原始背压专项及宽 recorder 的 metrics/tracing/static/dynamic 性能代表，明确记录完整矩阵未完成。
- 此前 `966041a9` 的 c128 P99.9 从 62.097 增至 67.918 µs（+9.374%，绝对 +5.821 µs），c512 为 −11.908%；这些是同一候选开启/关闭采样的对照，不是 2.0.3/2.0.2 的版本对照。轮间波动显著，但没有证明差异全部来自噪声或已查明 GC/调度根因；不能将此例外写成原门禁已通过。最终提交须保存自身完整结果，不拼接旧提交证据。
- 其他要求全部保留：完整正确性、安全扫描、八包/符号/版本/依赖/提交身份、空缓存安装、AOT、三平台 Release Gate、约 10 分钟 soak/ABI、分支保护及发布环境。全部硬条件正常通过后才创建标签/Release 和发布 NuGet。有限性能预算不是整个发布流程的总耗时。

## GitHub 门禁

日常功能通过 PR 合并到 `dev`。正式候选以 `dev → main` Release PR 收口；该 PR 自动运行三平台 Release Gate、NativeAOT、包安装和 Chaos。合并后若 SHA 变化，必须在最终 `main` 提交手工重跑 Release Gate，构建、包和性能证据不能沿用不同 SHA 的 PR head 结果。

创建标签前确认：

- `main` 上准备打标签的目标提交与本地冻结提交一致，并且完整 SHA 已通过 Release Gate；
- 分支保护要求评审和所有必需检查；
- GitHub 私有漏洞报告已启用；
- NuGet.org Trusted Publishing 已为仓库、`release-gate.yml` workflow 和 `release` 发布环境配置；
- Release notes 与 `CHANGELOG.md` 一致，预发布标记正确。

标签采用 `v<package-version>`。标签和 GitHub Release 必须在所有必需门禁通过后创建（2.0.0 的 5 小时异步长稳按上述明确例外处理），不使用标签来试跑尚未确认的候选代码。

## 首次 Trusted Publishing 配置

这一步由仓库和 NuGet.org 管理员在首次发布前完成一次，不能由本地提交代替：

1. 在 GitHub 仓库 `Settings → Environments` 创建 `release` Environment；建议配置 Required reviewers，并添加环境 secret `NUGET_USER`，值为 NuGet.org profile username（不是邮箱，也不是 API key）。
2. 在 [NuGet.org Trusted Publishing](https://www.nuget.org/account/trustedpublishing) 创建 policy：Repository Owner=`SunSi12138`、Repository=`SharpLink`、Workflow File=`release-gate.yml`、Environment=`release`。Policy 的个人或组织所有权必须与九个 SharpLink 包的实际 NuGet.org owner 一致。
3. 启用 GitHub Private vulnerability reporting、Dependabot alerts 与 dependency graph；首次合并 CodeQL workflow 后确认 Security 页面产生 C# 分析结果。把 `release-gate.yml` 设为标签发布前的必需检查。私有仓库的新 policy 需在其临时有效期内完成第一次成功发布。
4. 在首次正式标签前先用本地 `dotnet pack Sharplink.slnx -c Release -o artifacts/nuget` 和 `./eng/verify-packages.sh artifacts/nuget` 检查包；只有 policy 与 Environment 都就绪后才推送发布标签。

## 发布与回滚

`release-gate.yml` 只在 `v*` 标签触发、全部三平台测试/AOT/包安装/Chaos 门禁通过后进入受保护的 `release` Environment。发布 job 下载同一次运行产出的 `.nupkg` 和 `.snupkg`，使用 NuGet.org OIDC Trusted Publishing，不保存长期 API key；手工触发 Release Gate 只验证，不发布。推送前再次校验每个包的 ID、版本、SHA 和符号包配对关系，并按 `Abstractions → GenerationControl → Runtime → Sdk/Serializer → Client/Server → Hosting` 顺序发布。

NuGet 包不可覆盖或删除来替代修复。若发布内容有缺陷：

1. 立即停止继续推广并记录影响范围；
2. 对错误版本执行 deprecate（需要时给出替代版本）；
3. 从已发布提交创建最小修复，重复完整门禁并发布新的补丁版本；
4. 安全事件按 `SECURITY.md` 协调披露。

## RC 后允许延后

下列项目提升维护成熟度，但不改变当前二进制正确性，可以在不阻塞 RC 的前提下继续完善：持续 fuzzing、OpenSSF Scorecard、包签名/构建证明、跨机器长期性能实验，以及更多社区模板本地化。它们不能替代上述正确性、安全、包安装、Chaos 和性能门禁。
