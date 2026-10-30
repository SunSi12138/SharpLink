# 发布流程

本文定义 SharpLink `2.0` 及后续版本的正式发布门禁。发布对象必须是一个已提交、工作区干净且可由标签唯一定位的精确提交；RC 性能数字不能来自标签前后的近似版本。

## 版本与兼容性

- NuGet 包版本由 `VersionPrefix` 和可选的 `VersionSuffix` 组成。稳定版不设置后缀；预发布版本可使用例如 `rc7` 的后缀。
- `AssemblyVersion` 与 `FileVersion` 始终使用四段纯数字；预发布后缀只进入包版本和 `InformationalVersion`。
- 冻结前更新 `CHANGELOG.md`、包引用示例和迁移说明。公开 API、Protocol v2、生成代码、契约 Manifest 或默认行为的变化必须明确标注兼容性。
- `2.0.0` 公开 API 基线是 2.x 历史基线；3.0 是破坏性 major 升级，不以保持 2.0.3 API/二进制兼容为门禁。发布包 API 对照用于审阅和迁移证据，不代表无破坏变更承诺。补丁版本仍不得进行破坏性 API 或 wire 变更。
- 3.0 的 Generated ABI 为 API 5 / 精确 owner-bound identity，网络仍为 Protocol v2 minor 4；这两条版本轴分别验收。
- `eng/verify-protocol-v2-cross-version.sh` 保留历史路径，但实际验收同一批当前 NuGet 包的双进程互操作，不是真实 2.0.3 ↔ 3.0 矩阵。必须保留当前包成功路径及不兼容 minor、required capability、ABI identity、契约 hash 的拒绝测试；不自行扩大跨 major 成功互操作承诺。测试存在或文档列出范围不等于最终候选已经通过。

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

### 当前 3.0 候选的有限验证记录（未发布）

以下为 2026-10-30 的验证快照，不是发布日期或性能验收批准。负责人仅授权先运行有限范围、看结果再决定；尚未接受已知退化，也未批准完整矩阵的替代验收、merge、tag 或 NuGet 发布。三项性能实验的候选生产源码均固定为 `8ae9ee36ebec82f82e03185432601a830c865e95` / tree `1f0c9291c7394d012d3241a9cf31482b0734b697`；它们不是后续文档/诊断集成提交或最终 `main` 的新门禁结果。

**正式 2.0.3 对照：[O203N4 run 41013106170](https://github.com/SunSi12138/SharpLink/actions/runs/41013106170)。** 正式 2.0.3 与候选各用自身五个核心包及 Generator，在同机 TCP server-stream 公共负载下比较；不是全部九个发布包或跨 major 互操作验证。SDK 为 10.0.102，实际测量 runtime 为独立 CoreCLR 10.0.14。60 个进程包含 4 个候选严格控制、8 个对称普通控制及 48 个测量进程；每格六组 AB/BA 配对、3 秒预热和 6 秒测量。下表是六个 **candidate/2.0.3 配对比值的中位变化**，不是 pooled 比值或两组中位数之比；QPS 指完成 stream 数/秒，P99 指完整 stream 延迟。

| server-stream 场景 | QPS 变化 | P99 变化 | Process-wide B/stream 变化 |
| --- | ---: | ---: | ---: |
| static-intercepted，1 item | −8.640% | +7.654% | −10.255% |
| dynamic-intercepted，1 item | +1.097% | +4.698% | −16.465% |
| static-intercepted，100 items | −0.987% | +0.265% | −11.559% |
| dynamic-intercepted，100 items | −2.681% | +2.218% | −16.595% |

B/stream 是客户端、服务端及测量编排的进程总分配/完成 stream，不是每 item 或单线程分配。静态 1-item 的六组 QPS 均下降、P99 均上升，不能用分配降低抵消。四格均未达到原 `+10% QPS / −20% P99` 目标；数据完整性审核通过不等于性能通过。

**发送刷新专项：[FLUSH1 run 41013193344](https://github.com/SunSi12138/SharpLink/actions/runs/41013193344)。** 这是开发基线 `57f06f172d29fa5cfd4ea8e9b86a23f122df24ab` 到上述候选的对照，不是正式 2.0.3 对照，也不是协作维护测试。Balanced、32/4096 B、c1，六种 ABC 顺序共 18 进程、180 轮、737,280 个样本。4 KiB no-update 配对中位为 QPS +9.606%、P99 −0.187%，但各组 P99 比值跨 0.173–2.791；本次基线 P99 绝对中位约 1,217 µs，与旧 #964 对照的约 162 µs 不同。不能据此认定等价、确定根因或解除旧 4 KiB/c1 的 QPS −31.86% / P99 +652.87% 风险。

**受限协作维护专项：[MTL1 run 41014954521](https://github.com/SunSi12138/SharpLink/actions/runs/41014954521)。** 同一候选 binary 比较 off、on-no-update、post-update；固定认证 TLS 两连接、同 session/held proxy，18 进程、90 轮、450,000 RPC，并验证六次真实兼容实现替换。相对 off 的配对中位 QPS/P99 分别为 on-no-update +1.277%/−0.492%、post-update +0.501%/−0.773%；单组 P99 恶化 +20.504%/+34.500% 仍保留。六次本地已准备维护操作耗时 41.313–42.972 ms、进程分配 257,352–324,912 B，单列且不含制品传输、编译、加载、初始建连，也不混入稳态。此为 #933 的受限实现维护，不是完整 #775 或 2.0.3 版本比较；checkpoint I/O/JSON 与保留对象可扰动后续轮次，不证明零开销或等价。

完整 378-shard 性能矩阵尚未完成；上述结果不豁免历史 #931/#964 不利结果，也不构成对当前退化的接受。原始失败、异常组及负面样本保留，不能只用有利 pooled 数字替代配对结果。三项 run 的临时 Actions artifacts 仅保留三天，run 链接不是永久原始数据存档；哈希不能恢复过期文件。MTL1 的实际 build DLL/config 已归档校验，但 runtime 原始字节未归档，仅保留安装及 loaded-hash 记录。

**iOS 与最终门禁。** [诊断 run 41019690689](https://github.com/SunSi12138/SharpLink/actions/runs/41019690689) 和 [诊断 run 41022098807](https://github.com/SunSi12138/SharpLink/actions/runs/41022098807) 的 iOS CoreCLR（.NET 11 preview）simulator x64 均实际完成 codec produce/verify；每次为 58 个 producer fixture、98 个兼容验证结果，既有 builtin raw 排除项和单字节 segmented 不适用项保留。iOS arm64 当前仍未通过；既有 [mobile workflow](../.github/workflows/codec-mobile-compatibility.yml) 将 arm64 标为 experimental / continue-on-error，而 x64 为必需项，不因本次失败新增或放宽规则。诊断整体成功不表示 arm64 成功，也不替代旧 [Release Gate 41004413825](https://github.com/SunSi12138/SharpLink/actions/runs/41004413825) 的取消/summary 失败。最终文档与 iOS 诊断改动集成后的新候选完整 Gate、以及获准合并后的最终 `main` 精确提交 Gate 仍待完成；普通验证可继续，性能风险处置与发布授权分别待决定。

### 仅限 2.0.3 的性能验收例外

2026-10-04，发布负责人在了解原采样干扰门禁失败及尚未证实的根因后，明确接受本次有限性能验收并要求继续发布工作。此例外只适用于 `2.0.3`，不改变后续版本的默认完整矩阵或采样器原分析器。

- 在最终干净 `main` 的精确提交重建并核对实际 DLL 身份，执行原 micro 1M × 5 的全部 100 点、原 42 进程交替干扰对照和 Dual 准确性检查。原分析器、原阈值与所有失败值完整保留；仅 P99.9 的绝对 ±3% 失败作为本次接受并披露的性能局限，不再单独阻止发布。吞吐/P99 的绝对 ±3%、分配 ≤0.001 B/record、零错误、准确性、样本完整和排空仍是硬条件。
- 执行 36 个单轮正式采样探针：TCP/SharedMemory × Balanced/LowLatency/Throughput × Add/Echo32B/OneWay × c128/c512，池 1/1、发送队列 64 MiB、5 秒预热、10 秒测量、容量 300M；保留正常 OneWay 有界重试语义。零错误、完整样本、资源排空、精确提交及无超时必须通过，不把单轮结果称为完整五轮基线。
- 有限性能部分预计约 20–25 分钟，总预算 30 分钟；超时无效，不自动重试。省略 UDS/NamedPipe/AnonymousPipe、池 1/4、更大 payload、完整矩阵五轮、Async/Stream 性能、原始背压专项及宽 recorder 的 metrics/tracing/static/dynamic 性能代表，明确记录完整矩阵未完成。
- 此前 `966041a9` 的 c128 P99.9 从 62.097 增至 67.918 µs（+9.374%，绝对 +5.821 µs），c512 为 −11.908%；这些是同一候选开启/关闭采样的对照，不是 2.0.3/2.0.2 的版本对照。轮间波动显著，但没有证明差异全部来自噪声或已查明 GC/调度根因；不能将此例外写成原门禁已通过。最终提交须保存自身完整结果，不拼接旧提交证据。
- 其他要求全部保留：完整正确性、安全扫描、八包/符号/版本/依赖/提交身份、空缓存安装、AOT、三平台 Release Gate、约 10 分钟 soak/ABI、分支保护及发布环境。全部硬条件正常通过后才创建标签/Release 和发布 NuGet。有限性能预算不是整个发布流程的总耗时。

## GitHub 门禁

日常功能通过 PR 合并到 `dev`。正式候选以 `dev → main` Release PR 收口；该 PR 自动运行三平台 Release Gate、NativeAOT、包安装和 Chaos。合并后的 `main` push 自动在最终 `main` 精确提交运行 Release Gate，构建、包和性能证据不能沿用不同 SHA 的 PR head 结果。

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
