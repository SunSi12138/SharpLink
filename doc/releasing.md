# 发布流程

本文定义 SharpLink `1.0` 及后续版本的正式发布门禁。发布对象必须是一个已提交、工作区干净且可由标签唯一定位的精确提交；RC 性能数字不能来自标签前后的近似版本。

## 版本与兼容性

- NuGet 包版本由 `VersionPrefix` 和可选的 `VersionSuffix` 组成。例如 RC3 使用 `1.0.0` 与 `rc3`，得到 `1.0.0-rc3`。
- `AssemblyVersion` 与 `FileVersion` 始终使用四段纯数字；预发布后缀只进入包版本和 `InformationalVersion`。
- 冻结前更新 `CHANGELOG.md`、包引用示例和迁移说明。公开 API、Protocol v2、生成代码、契约 Manifest 或默认行为的变化必须明确标注兼容性。
- `1.0.0` 发布后保留其公开 API 包作为后续 `PackageValidationBaselineVersion`；不在补丁版本中进行破坏性 API 或 wire 变更。

## 本地冻结门禁

在干净工作区记录分支、完整提交 SHA、SDK/runtime、OS、架构和 CPU，然后依次完成：

1. 强制还原并执行非增量 Release 构建，要求零警告、零错误。
2. 执行 Generator、Unit、Integration 全套测试；Integration 必须覆盖真实传输、TLS/mTLS、认证授权、取消、deadline、流式背压、接入控制、优雅排空和故障恢复。
3. 打包全部七个 NuGet 包并确认：版本一致、依赖版本正确、仓库提交正确、主程序集具有 XML 文档、符号包具有 portable PDB、SDK 包具有 Generator。
4. 使用空 NuGet 缓存执行 `SharpLink.PackageSmoke`，避免项目引用或开发机缓存掩盖缺包。
5. 在支持的平台执行独立进程 SharedMemory NativeAOT smoke；其余平台由 Release Gate 矩阵完成。
6. 执行 24 小时 release soak。任何非注入错误、崩溃、恢复超时或结束后资源未归零均阻断发布。
7. 在同一精确提交执行 [最终性能矩阵](performance.md)，保存原始 JSON 和环境快照，只把可复现汇总写入仓库。
8. 执行传递依赖漏洞和弃用扫描；高危漏洞或运行时可达的中危漏洞必须在发布前解决。

## GitHub 门禁

日常功能通过 PR 合并到 `dev`。正式候选以 `dev → main` Release PR 收口；该 PR 自动运行 PR Quick、三平台 Release Gate、NativeAOT、包安装和 Chaos。合并后若 SHA 变化，必须在最终 `main` 提交手工重跑 Release Gate，构建、包和性能证据不能沿用不同 SHA 的 PR head 结果。

创建标签前确认：

- `main` 上准备打标签的目标提交与本地冻结提交一致，并且完整 SHA 已通过 Release Gate；
- 分支保护要求评审和所有必需检查；
- GitHub 私有漏洞报告已启用；
- NuGet.org Trusted Publishing 已为仓库、`release-gate.yml` workflow 和 `release` 发布环境配置；
- Release notes 与 `CHANGELOG.md` 一致，预发布标记正确。

标签采用 `v<package-version>`。标签和 GitHub Release 必须在所有门禁通过后创建，不使用标签来试跑尚未确认的候选代码。

## 首次 Trusted Publishing 配置

这一步由仓库和 NuGet.org 管理员在首次发布前完成一次，不能由本地提交代替：

1. 在 GitHub 仓库 `Settings → Environments` 创建 `release` Environment；建议配置 Required reviewers，并添加环境 secret `NUGET_USER`，值为 NuGet.org profile username（不是邮箱，也不是 API key）。
2. 在 [NuGet.org Trusted Publishing](https://www.nuget.org/account/trustedpublishing) 创建 policy：Repository Owner=`SunSi12138`、Repository=`SharpLink`、Workflow File=`release-gate.yml`、Environment=`release`。Policy 的个人或组织所有权必须与七个 SharpLink 包的实际 NuGet.org owner 一致。
3. 启用 GitHub Private vulnerability reporting、Dependabot alerts 与 dependency graph；首次合并 CodeQL workflow 后确认 Security 页面产生 C# 分析结果。把 `release-gate.yml` 设为标签发布前的必需检查。私有仓库的新 policy 需在其临时有效期内完成第一次成功发布。
4. 在首次正式标签前先用本地 `dotnet pack Sharplink.slnx -c Release -o artifacts/nuget` 和 `./eng/verify-packages.sh artifacts/nuget` 检查包；只有 policy 与 Environment 都就绪后才推送发布标签。

## 发布与回滚

`release-gate.yml` 只在 `v*` 标签触发、全部三平台测试/AOT/包安装/Chaos 门禁通过后进入受保护的 `release` Environment。发布 job 下载同一次运行产出的 `.nupkg` 和 `.snupkg`，使用 NuGet.org OIDC Trusted Publishing，不保存长期 API key；手工触发 Release Gate 只验证，不发布。推送前再次校验每个包的 ID、版本、SHA 和符号包配对关系，并按 `Sdk → Abstractions → Runtime/Serializer → Client/Server → Hosting` 顺序发布。

NuGet 包不可覆盖或删除来替代修复。若发布内容有缺陷：

1. 立即停止继续推广并记录影响范围；
2. 对错误版本执行 deprecate（需要时给出替代版本）；
3. 从已发布提交创建最小修复，重复完整门禁并发布新的补丁版本；
4. 安全事件按 `SECURITY.md` 协调披露。

## RC 后允许延后

下列项目提升维护成熟度，但不改变当前二进制正确性，可以在不阻塞 RC 的前提下继续完善：持续 fuzzing、OpenSSF Scorecard、包签名/构建证明、跨机器长期性能实验，以及更多社区模板本地化。它们不能替代上述正确性、安全、包安装、Chaos 和性能门禁。
