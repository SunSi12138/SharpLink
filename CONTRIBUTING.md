# 贡献指南

感谢你对 SharpLink 的关注与贡献！

## 本地开发

### 环境要求
- .NET SDK 10.0（`global.json` 固定当前 feature band）
- 建议使用最新稳定版 Rider / VS Code / Visual Studio

### 克隆与初始化
```bash
git clone https://github.com/SunSi12138/SharpLink.git
cd SharpLink
dotnet restore Sharplink.slnx
```

### 常用开发命令
```bash
# 构建
dotnet build Sharplink.slnx -c Debug -v minimal

# 验证生产项目引用架构边界（与 PR Fast 相同）
python3 eng/check-project-reference-boundaries.py

# 验证可维护性 debt baseline（与 CI 使用同一入口）
bash eng/check-maintainability.sh

# 运行示例
dotnet run --project demo/HelloWorld
dotnet run --project demo/Streaming

# 运行单元测试（TUnit）
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj

# 运行集成测试
dotnet run --project test/SharpLink.IntegrationTests

# 生成并验证 NuGet 主包、XML 文档和符号包
dotnet pack Sharplink.slnx -c Release -o artifacts/nuget
./eng/verify-packages.sh artifacts/nuget
```

## 测试约定

- 单元测试放在 `test/SharpLink.UnitTests`，优先覆盖纯逻辑与边界场景。
- 集成测试用于验证端到端链路，不替代单元测试。
- 新增/修改核心功能时，请至少补充一条对应测试。
- 仓库内所有项目均执行零警告策略：编译器、分析器和 NuGet audit 警告均视为错误；`src/` 公共 API 缺失 XML 注释同样视为错误。
- RPC 热路径、传输、生成代码或序列化变更需要记录精确基线和候选配置，并证明无实质性能回退。

## 架构与可维护性约定

SharpLink 把可执行 guard 作为规则入口，把文档作为规则解释；贡献指南不复制会随工具演进的阈值或完整策略。

- 生产项目引用边界：运行 `python3 eng/check-project-reference-boundaries.py`。规范性策略是 [`doc/project-reference-boundaries.yml`](doc/project-reference-boundaries.yml)，人类可读说明见 [`doc/project-reference-boundaries.md`](doc/project-reference-boundaries.md)。新增/删除生产项目、增加/删除 `ProjectReference` 或改变引用模式时，如果意图确实改变架构边界，必须在同一 PR 更新规范策略；不能绕过 guard。
- 可维护性 debt baseline：运行 `bash eng/check-maintainability.sh`。baseline 格式、例外语义和 review 规则以 [`eng/maintainability.md`](eng/maintainability.md) 与 [`eng/maintainability/baseline.json`](eng/maintainability/baseline.json) 为准；不要在其他文档复制具体阈值。
- PR 的快速验证范围与本地等价命令见 [`doc/pr-fast.md`](doc/pr-fast.md)。提交前至少运行与变更相关的 guard 和测试。

### 行为保持重构

- 行为保持重构应保持现有公共 API、线协议、生成 ABI，以及可观察的错误、取消、deadline、顺序和并发语义；若这些语义需要改变，应把行为变更显式写入 PR，并提供相应测试/文档。
- 优先沿独立状态、明确不变量、生命周期或资源所有权提取类型/组件，使新边界能够单独测试和解释。
- 不要仅为了降低物理文件 LOC 或通过 baseline gate，把同一职责机械拆成多个 `partial` 文件。若状态、耦合和所有权没有得到更清晰的边界，这种拆分不视为有效的可维护性改进。
- 尽量把机械整理与行为变更分开，避免用大范围重构掩盖协议、并发或性能语义变化。

### 例外与 baseline 变更

大型文件或 baseline 例外不是常规扩展机制。只有在当前历史 debt 仍需保留，或同一聚焦 PR 内立即拆分会明显扩大范围、增加行为/兼容性/性能风险且没有安全的小步提取路径时，才应考虑新增或提高 allowance。

Review baseline 变更时：

- 先确认是否可以通过独立状态/不变量/所有权提取消除例外，而不是扩大 allowance。
- 新增或提高 allowance 必须在 baseline 中提供非空 `reason`，并按 [`eng/maintainability.md`](eng/maintainability.md) 的规则保持无额外 headroom；review 应明确检查该例外的范围和必要性。
- 文件删除或回落到正常策略范围后应移除陈旧 allowance；全局阈值变化属于策略变化，需要单独、显式的 review 理由。
- 架构临时例外必须在规范边界策略中显式记录理由和 tracking provenance，并保持架构 guard 通过；不能用条件、传递依赖或“临时先过”作为未记录例外。
- 如果例外实际固化了长期的性能、NativeAOT、协议、并发或所有权取舍，应同时记录 ADR，而不是只在 baseline/YAML 中留下结果。

## ADR 约定

对不明显、会长期约束后续实现的性能、NativeAOT、协议、并发或所有权决定，使用轻量 Architecture Decision Record。约定见 [`doc/adr/README.md`](doc/adr/README.md)，可复制模板见 [`doc/adr/0000-template.md`](doc/adr/0000-template.md)。

ADR 用来记录背景、决定、主要取舍和后果，而不是复制可执行规则。涉及维护性阈值、架构边界、CI 命令或其他已有 canonical policy 时，ADR 应链接到对应来源。局部实现选择、明显 bug 修复或不形成长期约束的机械整理通常不需要 ADR，PR 描述即可。

## 代码与提交规范

- 尽量保持变更最小化，避免无关重构混入。
- 命名清晰，避免单字符变量名。
- 保持项目现有风格，不引入不必要依赖。
- 提交信息建议使用清晰前缀，例如：
  - `feat: ...`
  - `fix: ...`
  - `test: ...`
  - `docs: ...`

## Pull Request 规范

提交 PR 前请确认：
- 能在本地完成构建与相关测试。
- 变更内容与 PR 描述一致，说明动机与影响范围。
- 仓库唯一的文档根目录是 `doc/`；新增文档请放入 `doc/`，不要创建并行的 `docs/` 目录。
- 若涉及行为变更，补充文档（如 `README.md`、`doc/*`、`CHANGELOG.md`）。

PR 描述建议包含：
- 变更背景
- 核心改动点
- 测试方式与结果
- 兼容性影响（如有）

## Issue 与沟通

- Bug 报告请尽量提供复现步骤、环境信息、期望结果与实际结果。
- 功能建议请说明使用场景和预期收益。
- 安全漏洞不得通过公开 Issue 报告，请使用 [`SECURITY.md`](SECURITY.md) 中的私有渠道。
- 正式版本的冻结、包验证、Chaos、性能、标签和回滚规则见 [`doc/releasing.md`](doc/releasing.md)。
