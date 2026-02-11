# 贡献指南

感谢你对 SharpLink 的关注与贡献！

## 本地开发

### 环境要求
- .NET SDK 10.0（与仓库当前目标框架一致）
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

# 运行示例
dotnet run --project demo/HelloWorld
dotnet run --project demo/Streaming

# 运行单元测试（TUnit）
dotnet test --project test/SharpLink.UnitTests/SharpLink.UnitTests.csproj

# 运行集成测试
dotnet run --project test/SharpLink.IntegrationTests
```

## 测试约定

- 单元测试放在 `test/SharpLink.UnitTests`，优先覆盖纯逻辑与边界场景。
- 集成测试用于验证端到端链路，不替代单元测试。
- 新增/修改核心功能时，请至少补充一条对应测试。

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
- 若涉及行为变更，补充文档（如 `README.md`、`doc/*`、`CHANGELOG.md`）。

PR 描述建议包含：
- 变更背景
- 核心改动点
- 测试方式与结果
- 兼容性影响（如有）

## Issue 与沟通

- Bug 报告请尽量提供复现步骤、环境信息、期望结果与实际结果。
- 功能建议请说明使用场景和预期收益。
