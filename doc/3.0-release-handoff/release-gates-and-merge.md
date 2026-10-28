# SharpLink 3.0 发布门禁与合并执行手册

状态：**尚未达到发布条件**。本手册基于 2026-10-28 14:05（Asia/Shanghai）只读检查，入口为 `artifacts/release-readiness/2026-10-28/progress.json`，当时 dev 为 `eec9524aa21251a1d548f33e363dd4c51094cb14`。后续状态以 root 更新的交接总表、远端 PR head 和实际工件为准。

目标仍是完成 SharpLink 3.0 的原定验收并真实发布。订阅到期是交接期限，不是允许跳过门禁的发布期限。所有以下命令均是待执行步骤；本次只编写手册，没有运行重型门禁、推送、合并或打标签。

## 1. 权限和停止条件

- **机器可完成（M）**：源码/日志检查、实现已授权范围内的修复、测试、收集完整证据、按已明确授权的流程开 PR/触发 CI/合并。每个 PR 必须由独立子代理审核精确 head；实现者、设计参与者不能充当首次独立审核者。
- **必须人类裁决（H）**：缩小原定 3.0 范围、接受已知性能回退或安全/正确性缺口、改变阈值/协议承诺、增加显著付费算力、变更管理员安全策略、绕过保护。不得把时间不足解释成已获准裁剪范围。
- 阻断意味着停止该发布动作，不是停止其他独立工作。`NoGo`、不完整样本、未归因错误、未交付原始功能均保持真实状态。CI success 不能替代性能裁决、API 语义审批或范围接受。
- 禁止标签试跑；禁止删除失败样本、改阈值、重复刷绿、把 smoke 标为 full。确有新诊断或修复才开始新 attempt，保留旧 attempt 与原因。

## 2. 先收敛产品与专项，后冻结

### 建议的依赖顺序

1. **M — 先提交和独立审核工具 PR**：#931 消融工具、#732 三臂工具分别拥有自己的精确源码身份、同源 controls、预声明样本和分析判据。由 root 协调既有 owner；工具通过不等于产品通过。不能把尚在其他 worktree 的脚本当作 dev 上已存在的入口。
2. **M — #931 先裁决，再合入**：当前产品 head `ac17c8d8ea0203edbe7b8cf19ddb95eaa24d4d2c`；48/48 run `40634900826` 的独立统计结论为 NoGo，dynamic100 吞吐 −2.67%、CPU +2.85%、P99 +38.08%、P99.9 +21.64%。owner 的 FullOpts、candidate Runtime/Server 配 old/new Client 消融回答原因，不自动推翻端到端结果。修复后重新审核受影响生命周期和对应配对证据；不得仅凭正确性 CI 合并。重大回退接受属于 H。
3. **M — #933 在已裁决 #931 上收敛**：当前远端修复头 `10b1bf03881337c0801d6ff24318bb479747ca6c` 包含 #931 前置；产品修复在 `50e608ca`，`10b1bf03` 仅修格式。独立审核通过，本地 Unit 2439 / Integration 557 全通过；查询当前头 canonical CI，不能把旧头的结果改名。旧 `40636189267` 的 Integration 530 pass/26 fail、native `OnRegistered` Tier1 缺失仍保留。不能先合 #933 间接绕过 #931 性能阻断。
4. **M — #732 三臂判决后集成**：pre-M / M0 / M1 三臂及 same-source controls 各自不可替换。M0 已 NoGo；受主机竞争污染的本地 T/U 结果为 inconclusive，不升格。选择有证据支持的候选后才将产品改动带到最终集成；旧 #754 不是自动可合候选。与 #933 的 bridge/dispatch 冲突由同一集成 owner 解，不并行改同一状态机。
5. **M — API 台账与文档最终绑定**：#953 已合入仅代表该批提案及覆盖工具获审核，不代表最终 #732/#931/#933 的新 API 自动批准。最终库存、实包 diff、逐类型理由、签名变化与迁移一起走独立 PR 审核。
6. **M/H — 范围与依赖收口**：#775 完整 Binding/receipt 尚未交付。保留 issue OPEN、原验收清单和 #861/#862 研究证据；不能因 #933 合并关闭 #775。用户允许部分 issue 保持开放；不能仅凭 #775 仍 open 新增一条发布阻断。核对已有讨论、用户授权和实际 3.0 承诺：明确承诺纳入本次发布而未交付的项必须完成，或取得用户对范围调整的明确决定；不能将受限 #933 描述成完整 #775。依赖 PR #944–#948 逐项审阅适用性，不批量合并；#949/#950 是 main 上分开的 CodeQL init/analyze 更新，dev #952 已成组解决，dev→main 后核实再处理重复 PR。

### 每次 PR 合并前

```bash
cd /Users/sunsi/Documents/ChatGPT/SharpLink
gh pr view "$PR" --json number,headRefOid,baseRefName,isDraft,mergeStateStatus,reviewDecision,statusCheckRollup,url
gh pr diff "$PR" --name-only
gh pr checks "$PR"
```

记录 reviewer、审核 head、问题结论、命令/测试名/实际数量、CI run 与 artifact。若 head 更新，先比较旧 head→新 head；独立 reviewer 复核新增代码及其直接受影响契约。rebase/merge 冲突解析也算代码变化；只改文档可限定语义复核，不能把旧性能/包 SHA 改名。正式 merge 前再查一次 head，以 `gh pr merge "$PR" --merge --match-head-commit "$REVIEWED_HEAD"` 防止审核后 head 漂移；前提是当前用户授权与分支保护均满足。禁止 `--admin`。

### 冲突解决必须保留的契约

- #931 的物理工作直到真实 MoveNext/Dispose/回调清理结束才释放；逻辑 response/cancel 不能抢先释放；每 RPC 的原选择 reservation 不能复制成新计数器。
- #933 仅 compatible server implementation-only；完整依赖身份校验、registration retirement gate、queued admission 明确拒绝、封闭配置与固定认证 session roster、FIFO fence、全部 commit 后 resume、失败保持关闭。
- #930 queued registration identity；#932 send-backpressure telemetry/reentrancy；#935 writer-close-before-final-drain；#718 可选 GenerationControl 包边界。
- #951 reader fixture 提取、弱引用/异常 carrier/mapper 生命周期；现有 test project/reference 边界和 Explicit 诊断 worker 不能当普通测试消失。
- #732 timed/untimed request shape 与 deadline/cancellation ownership；Generator/ServerGeneratedBridge/RpcSession.GeneratedServerBridge 的事实字段必须一致。
- #933 新 retirement-only 处理不得吞掉 referenced codec owner 不匹配；保持负对照。禁止用跨用户 metadata/SPI callback 的大锁遮蔽竞争。
- 九包/API5 identity、#953 台账的精确 ID、已合成组 CodeQL init/analyze 版本和现有正负控件。

冲突后下一动作：按差异文件列出对应状态转移/生成器→消费者契约，交原 owner 修复，独立 reviewer 查看实际解析结果，再跑受影响 focused 测试和该产品 canonical CI；不是 `ours/theirs` 全文件选择。

## 3. 冻结精确候选与记录证据（M）

只在上述产品、范围和文档决策完成后选择冻结 SHA。下例在**干净的专用候选 checkout**执行，不能在其他 owner 的未提交工作区操作。

```bash
set -euo pipefail
cd /Users/sunsi/Documents/ChatGPT/SharpLink
CANDIDATE=$(git rev-parse HEAD)
test -z "$(git status --porcelain --untracked-files=normal)"
EVIDENCE="$PWD/artifacts/release-readiness/$CANDIDATE"
mkdir -p "$EVIDENCE"
printf '%s\n' "$CANDIDATE" > "$EVIDENCE/source-commit.txt"
git branch --show-current > "$EVIDENCE/branch.txt"
dotnet --info > "$EVIDENCE/dotnet-info.txt"
uname -a > "$EVIDENCE/os.txt"
# Linux: lscpu；macOS: sysctl -n machdep.cpu.brand_string；Windows: Get-CimInstance Win32_Processor。
```

同时记录命令、UTC 起止时间、OS/架构/CPU、实际 SDK/runtime、干净状态、CI run ID/headSHA、所有参数、报告路径和退出码。`global.json` 请求 10.0.102/latestPatch，不代表各 runner 实际都相同；配对实验必须固定实际版本。最终机器码/DLL 身份属于发布性能来源要求，使用现有报告的身份信息，缺项才补实际被执行文件身份；不为普通文本检查添加散列。

下文默认 `EVIDENCE` 指候选新目录；有固定输出目录的脚本可能清理旧输出，重跑前先完整归档旧目录。禁止置 `SHARPLINK_ALLOW_DIRTY_PACKAGE_VERIFY=true`。

## 4. 正确性、九包与 API/ABI 门禁（M；API 接受需独立语义审核）

### 4.1 全量构建和测试

```bash
dotnet restore Sharplink.slnx --force --no-cache
dotnet build Sharplink.slnx --no-restore --no-incremental -c Release -v minimal
for name in SharpLink.UnitTests SharpLink.Generator.Tests SharpLink.LoadTest.Tests SharpLink.StreamLoadTest.Tests; do
  dotnet test --project "test/$name/$name.csproj" -c Release --no-build
done
dotnet run -c Release --no-build --project test/SharpLink.IntegrationTests -- \
  --maximum-parallel-tests 1 --timeout 120s --output Detailed \
  --github-reporter-style full --no-progress \
  --results-directory "$EVIDENCE/integration" \
  --junit-output-path "$EVIDENCE/integration/junit.xml" \
  --diagnostic --diagnostic-verbosity Trace --diagnostic-output-directory "$EVIDENCE/integration"
bash eng/verify-generated-assembly-dependencies.sh
bash eng/verify-runtime-architecture.sh
```

这是仓库 .NET 10 native MTP/TUnit 语法，非旧 VSTest。通过：构建零 warning/error；每个预声明正常测试已执行且无失败/非预期 skip；TLS/mTLS、认证授权、取消/deadline、五种 RPC、背压/接入控制、排空恢复、ALC/真实 transport 必须在实际库存内。显式诊断测试按既有规则列明，不通过删列表凑 pass 数。

失败下一步：Integration 先读 `junit.xml` 与 Trace 中第一个失败的注册/codec/retirement 事件，区别根因和连带 missing-manifest；单例用真实发现的 UID 复现。构建失败先定位第一个诊断；不要先全套重跑。

### 4.2 九包闭包和实包 API

```bash
dotnet pack Sharplink.slnx --no-build --no-restore -c Release -o artifacts/nuget -v minimal
bash eng/verify-packages.sh artifacts/nuget 3.0.0
bash eng/verify-hosting-package-dependency.sh artifacts/nuget
bash eng/verify-abstractions-package-dependency.sh artifacts/nuget
bash eng/verify-public-api.sh artifacts/nuget 3.0.0
python3 eng/verify-release-versions.py
python3 eng/compare-published-api.py artifacts/nuget \
  --current-commit "$CANDIDATE" --output "$EVIDENCE/published-api-2.0.3"
python3 eng/run-api-inventory.py --output "$EVIDENCE/api-inventory"
python3 eng/verify-api-decision-ledger.py "$EVIDENCE/api-inventory"
python3 -m unittest discover -s eng/tests -p 'test_api_decision_ledger.py' -v
```

九包：Abstractions、GenerationControl、Runtime、Sdk、Serializer.SharpPack、Client、Compression.Zstd、Server、Hosting。每包恰好一个 nupkg+snupkg；版本 3.0.0、nuspec commit、portable PDB/程序集配对、XML、SDK 内 Generator、依赖版本闭包一致。不要将 Generator 单独算第十个发布包。`verify-packages.sh` 不替代人工核对额外包/完整依赖图；读取全部 nuspec 中 SharpLink.* 依赖，确认没有旧版本或漏包。

当前台账绑定旧库存来源、SDK 10.0.113；新候选运行 `verify-api-decision-ledger.py` 因 provenance 改变失败可能是正确的停止。先比较新旧 symbols 的 ID **和签名/继承边/证据**，由台账 owner 更新两份 JSON 的精确绑定和必要条目，再独立审核；不可只替换 SHA 使验证变绿。`compare-published-api.py` 成功只表示生成差异证据，**差异本身不使脚本失败**，所以还要审核每一项变化、SDK forwarder、消费迁移。

正式版本事实源为 `eng/release-versions.json`（根目录没有同名文件）。在 eec9524a 基线，Generated API=5，runtime/Generator exact identity 为 `sharplink-3.0-api5-owner-bound-unsafe-blit-v1`，Protocol=2、minor=4、minimum=4、Manifest JSON=2。若采用 #732 complete-facts，其独立 API5 exact identity 为 `sharplink-3.0-api5-complete-method-facts-v1`；必须同步真实 Generator、Runtime、fixtures、迁移和版本台账，不能用本段基线文字覆盖最终设计。不能只比 API 数字而接受不同 API5 开发 identity。

失败下一步：包身份看该包 nuspec/PDB/DLL 与候选 SHA；API 看 `artifacts/public-api/current/public-api.diff` 与实包提取；台账看第一个缺失/额外/变签名 ID，回到对应源码和消费者，不自动刷新 baseline。

### 4.3 包安装、Trim 与 NativeAOT

既有 CI `release-gate.yml` 的 pack 调用 `.github/actions/package-smoke/action.yml`：Linux 空缓存 PackageSmoke、PackageTrimSmoke/full、PackagePartialTrimSmoke、PackageLateTrimSmoke 属性验证、PackageAotSmoke、Quick Start。`pr-package-smoke.yml` 额外调用 ProjectReference full/partial trim。三个 OS 的 Release Gate 运行 PackageSmoke 和进程 AOT，但**没有**现成三 OS packaged full/partial trim 矩阵。

本地/专用 runner 的准确入口（先 pack，缓存路径须新建空目录）：

```bash
export NUGET_PACKAGES=$(mktemp -d "$EVIDENCE/package-cache.XXXXXX")
dotnet restore test/SharpLink.PackageSmoke/SharpLink.PackageSmoke.csproj \
  --force --no-cache --configfile test/SharpLink.PackageSmoke/NuGet.config
dotnet run -c Release --no-restore --project test/SharpLink.PackageSmoke/SharpLink.PackageSmoke.csproj
bash eng/run-quickstart-package-smoke.sh artifacts/nuget
SHARPLINK_TRIM_RID="$RID" bash eng/run-project-reference-trim-smoke.sh
SHARPLINK_AOT_RID="$RID" SHARPLINK_AOT_OUTPUT="$EVIDENCE/aot-$RID" \
  bash eng/run-shared-memory-aot-process-smoke.sh
```

`RID` 在对应本机设置为 linux-x64 / win-x64 / osx-arm64，不跨机运行本机二进制。ProjectReference trim 脚本运行后会删除自身临时目录，必须保存 stdout/exit log；full 与 partial 均有 `PROJECT_REFERENCE_TRIM_SMOKE_PASS`。

补齐三 OS packaged trim/AOT 时逐个 consumer 复制以下真实入口（相应 OS shell 运行；Windows 末尾执行 `.exe`）：

```bash
for consumer in PackageTrimSmoke PackagePartialTrimSmoke PackageAotSmoke; do
  export NUGET_PACKAGES=$(mktemp -d "$EVIDENCE/$consumer-cache.XXXXXX")
  project="test/SharpLink.$consumer/SharpLink.$consumer.csproj"
  output="$EVIDENCE/$consumer-$RID"
  dotnet restore "$project" --force --no-cache \
    --configfile test/SharpLink.PackageSmoke/NuGet.config -r "$RID" /p:SelfContained=true
  dotnet publish "$project" --no-restore -c Release -r "$RID" --self-contained true \
    -o "$output" /p:TrimmerSingleWarn=false
  executable="$output/SharpLink.$consumer"
  if [[ "$RID" == win-* ]]; then executable="$executable.exe"; fi
  "$executable"
done
```

通过：真实执行成功、正确 round trip/拒绝对照成立、Trim/AOT warning-as-error 不被关闭；生成程序集无 Runtime 依赖。新 CI wiring 若用于补齐多 OS 门禁，单独 PR+独立 review 后再用；不能宣称目前 Release Gate 已覆盖这部分。

失败下一步：先查看具体 IL warning 或缺失 factory/ABI metadata，再区分 ProjectReference/PackageReference 的 props/targets 导入；禁止加全局 suppression 或将 PublishAot/PublishTrimmed 置 false。

### 4.4 ABI、wire 与 codec 兼容

```bash
bash eng/verify-generated-abi-mixing.sh
bash eng/verify-protocol-v2-cross-version.sh
```

ABI 输出 `artifacts/generated-abi-mixing`：当前与已发布 **1.1.1** 的两个混包负向组合必须被合法版本/ABI诊断拒绝，且同版本正向控件先成功。它不单独证明 2.0.3/API4→API5 行为。完整 Unit/Integration 中 `GeneratedManifestLocatorTests`、stale descriptor、`Api4BinaryFixtureIntegrationTests`、ALC 拒绝与释放测试另保留。

**已发现覆盖名实差异**：`verify-protocol-v2-cross-version.sh` 当前仅使用本次包构建 server/client，输出 `CLIENT_PASS`/`SERVER_PASS`；未装载真正 2.0.3 peer。它证明当前同版本真实进程 wire 路径，不能报告为 2.0.3↔3.0 跨版本通过。最终 wire 兼容承诺若包含此边界，需补真正双版本进程/期望拒绝控件并独立审核；这是待交付门禁，不存在可在此直接调用的现成完整脚本。

Codec CI：`codec-compatibility.yml` 6 个 desktop producer × 6 consumer，summary 必须收齐六份消费者报告；`codec-servicing-compatibility.yml` 精确 baseline/latest SDK/runtime 2×2；mobile required 为 Android x64 与 iOS CoreCLR x64，arm64 iOS 是实验臂；Browser Mono/CoreCLR 也是 continue-on-error 的实验事实。下载原报告，不能由 workflow 总绿推断实验臂成功或支持范围扩大。失败读 producer manifest 的 SDK/runtime/ABI 与首个 fixture 分类，不能把 raw-layout 不兼容改成成功。

## 5. 完整 JIT+NativeAOT 性能：先安排资源，不在单个 job 硬跑

### 实际规模与时限

`eng/run-performance-matrix.sh` 已支持环境分片，但默认 `smoke/jit`。完整默认：5 transport（Windows 4，除 UDS）、3 profile、5 repetitions；非 anonymous 两种 pool 1/1 与 1/4，anonymous 仅 1/1。Unary 六 payload 的并发 cell 为 `6+6+6+6+4+3=31`；固定 OneWay=1；JIT-only 原始背压=6；yield/delay=12；streams `all` 实际为 unary/c2s/s2c/duplex，4×6=24。每 cell 5 秒 warmup+20 秒测量。

- 非 Windows：135 个 transport/profile/pool/repetition 组合；JIT 74 + AOT 68 cells = **19,170 cells**；报告 `135×(11+10)=2,835 JSON`；纯计时下限 **133.125 runner-hours**。
- Windows：105 组合；**14,910 cells / 2,205 JSON / 103.542 runner-hours**。
- 若 Linux、macOS、Windows 各完整一套，仅计时合计 **369.792 runner-hours**，未含 build/AOT、建连、排空、分析、失败、新 attempt 或版本基线对照。双版本配对需要额外资源；不能把不同 runner 的两批绝对数字当同机配对。
- `.github/workflows/performance-evidence.yml` 固定 smoke/jit，仅 input `repetitions`、45 分钟 timeout。`nightly.yml` perf-smoke 也不是 full。不存在已核实的 full 自动 dispatch 工作流；不要向 smoke workflow 发送虚构 `tier=full` input。
- GitHub-hosted job 上限 **6小时**，见 [GitHub Actions limits](https://docs.github.com/en/actions/reference/limits)。单 job 全矩阵不可能完成；单 transport/profile/runtime 约 JIT 308分钟加启动/构建也过于贴近上限。

推荐复用脚本，以 **transport × profile × runtime × workload** 分片：非 Windows 135 个分片（AOT 排除 oneway-backpressure），Windows 108；每片仍五轮并保留默认全部 payload/并发/pool。最重 Unary 单片下限 129.17 分钟，streams 单片100分钟，建议新受审 workflow 的 job 预算240分钟且记录实际耗时。配置 fail-fast=false、失败也上传、唯一 artifact 名包括 SHA/shard/attempt。不要在同一机器并发测不同片；不同独占 runner 可并行。资源费用/可用并发尚未核实，超既有授权须 H 决定，不先购买。

### 一个分片的可执行入口

以下仅示范 tcp/balanced/jit/unary 一片，不能独立称 full：

```bash
performance_root="$PWD/artifacts/performance/3.0/$CANDIDATE"
mkdir -p "$performance_root"
attempt=$(mktemp -d "$performance_root/tcp-balanced-jit-unary.XXXXXX")
SHARPLINK_COMMIT="$CANDIDATE" \
SHARPLINK_MATRIX_TIER=full SHARPLINK_MATRIX_RUNTIMES=jit \
SHARPLINK_MATRIX_TRANSPORTS=tcp SHARPLINK_MATRIX_PROFILES=balanced \
SHARPLINK_MATRIX_WORKLOADS=unary SHARPLINK_MATRIX_REPETITIONS=5 \
SHARPLINK_MATRIX_OUTPUT="$attempt" bash eng/run-performance-matrix.sh
```

分片笛卡尔积：transports=`tcp,uds,namedpipe,anonymous,sharedmemory`（Windows去uds）；profiles=`balanced,lowlatency,throughput`；runtimes=`jit,aot`；workloads=`unary,oneway,oneway-backpressure,async,streams`（AOT去backpressure）。不要 override payload/concurrency/warmup/duration/stream operation 来省时间。300M recorder capacity、64MiB send queue 和背压 workload 的 profile-default queue 语义保持。

执行前保存完整预期 coverage 表，粒度包含 runtime/repetition/transport/profile/pool/workload/operation/payload/concurrency；汇总按该 key 检查缺失、重复、平台排除，不能只数 JSON。运行目录从不复用；脚本会删除同名报告并重新 publish AOT，同路径补跑会破坏失败证据。

**full 汇总器已实现，待 PR #954 的 hosted CI 与合并**：独立审核通过的工具为 `2422d3b40d9a64380c5f75cb6bcd87a67db9c17f`，工作树 `/Users/sunsi/.codex/worktrees/release-full-matrix-tools/SharpLink`。使用 `eng/performance-matrix.py` 的 `plan/run-shard/validate`，详见该分支 `eng/performance-matrix-acceptance.md`；17 项离线 fixtures 及真实无负载计划通过。GitHub 分片调度仍只是模板，没有部署。这与旧入口不同：现有 `--validate-performance-reports` CLI 只接受 `<SHA> <output> <matrix-dir> <stream-dir>`，并硬编码27份matrix+10份stream，专属 latency-recorder baseline；不能传一个 full 根目录或修改这两个计数假装支持 full。底层 `PerformanceReportValidator.AnalyzeDirectories` 可复用来源/样本/排空语义，但 full 需要单独受审的配置覆盖汇总入口，且原始背压报告要按其预声明饱和语义分类，不能把预期本地背压与非注入业务错误混为一类。下一动作：核对 #954 最终 head/CI，再在最终冻结候选上生成完整 manifest 并安排匹配平台分片；工具拒绝缺失 Failure 和复制报告冒充多轮。不要更改原 recorder 入口。

汇总只选择有明确接受结论的本次完整attempt；历史失败attempt单列而非混入或删掉。全部预期cell完整、零非预期错误、样本=成功操作、容量足够、无丢样/超时、真实排空、精确source/DLL/AOT身份必须成立。当前 `PerformanceReportWriter` 记录source、runtime/OS/架构/核数等，但**未记录SDK、CPU型号和实际执行DLL/AOT文件身份**；每个分片须额外保存这些来源和完整命令。此处二进制身份是原发布证据要求，不是一般文件哈希检查。OneWay为本地bounded acceptance，receiver business throughput另需完成计数，不能混称。

### 独立保留的性能门禁

```bash
dotnet build test/SharpLink.Benchmarks/SharpLink.Benchmarks.csproj -c Release
SHARPLINK_ALLOCATION_OUTPUT="$EVIDENCE/allocation-gate.json" bash eng/run-allocation-gate.sh
SHARPLINK_COMMIT="$CANDIDATE" SHARPLINK_RECORDER_RUNS=5 \
  SHARPLINK_RECORDER_MICRO_RECORDS=1000000 \
  bash eng/run-latency-recorder-baseline.sh "$EVIDENCE/latency-recorder"
```

recorder 入口要求全新、尚不存在输出目录；包含 micro、42 次 macro/off-formal-tail-dual、feature/matrix/stream 及原分析器。macro 吞吐/P99/P99.9 绝对变化均≤3%，micro median allocation≤0.001 B/record，Dual accuracy、完整样本/排空照原规则。2.0.3 的 P99.9 例外不适用3.0。allocation budgets 与负对照按 `.github/actions/allocation-gate/action.yml`，失败不能通过增预算解决。

**性能接受**：预先固定 baseline/candidate、环境、顺序、统计和阈值，同机交替全样本；当前专项 #732/#931/#933 的生命周期、代码尺寸/native 与匹配性能仍单独成立。没有显著差异不是已证明等价。失败下一动作：先定位失败 cell 的 sample/error/drain/CPU/toolchain，再对具体 changed path 做 owner 已审消融；区分环境无效与真实回退，二者都不直接接受。

## 6. GitHub 最终候选门禁、10分钟 soak 与 churn（M）

命令 `--ref` 使用**明确冻结的远端分支名**；不能在运行中移动该分支。dispatch 后立刻核对返回 run 的 headSha 等于 CANDIDATE。CLI 帮助要求 branch/tag ref，本手册不假设裸 SHA dispatch 受支持。

```bash
gh workflow run release-gate.yml --ref "$FROZEN_BRANCH"
gh workflow run pr-package-smoke.yml --ref "$FROZEN_BRANCH"
gh workflow run nightly.yml --ref "$FROZEN_BRANCH" -f release_soak=true
# nightly dispatch 已包含 servicing；若独立补跑：
gh workflow run codec-servicing-compatibility.yml --ref "$FROZEN_BRANCH"
```

记录每次实际 run URL/ID；只有缺失该项证据才独立补跑 servicing，不重复有效检查。查看/下载：

```bash
gh run view "$RUN_ID" --json headSha,status,conclusion,jobs,url
gh run download "$RUN_ID" -D "$EVIDENCE/github-$RUN_ID"
# 失败时：
gh run view "$RUN_ID" --log-failed
```

Release Gate job：matrix build/test 3 OS（45m）；AOT linux-x64/win-x64/osx-arm64（40m）；pack/API/ABI（30m）；allocation（12m）；Chaos 3 OS（10m）；codec desktop/mobile；`release-summary` 要求所有 required job success。它没有 full perf、release soak 或独立台账语义接受，不能视作全部验收。

`nightly.yml -f release_soak=true`：两个 Linux transport job tcp/sharedmemory，各30m job预算，实际 soak=10m、concurrency32、restart60s、checkpoint2m，输出 `artifacts/chaos/release-10m-{transport}.json` 与诊断；ABI churn 同 run，duration600秒，job预算135m，输出 `artifacts/p3-generated-abi/dynamic-module-soak/{tests.json,test.log,summary.txt}`。**只在GitHub跑长稳，本机不跑。**

通过：两种 transport 正常结束，实际>=600秒、恢复/排空/资源归零满足报告；无非注入错误、崩溃或恢复超时。ABI 每轮9个测试进程（replacement、route drain、5 stream exits、rejection、framework unload），rounds>0、duration_seconds>=600、完整末轮结束，可能超过10分钟；summary commit 必须匹配。不能说验证了5小时稳定性。

失败下一动作：保存对应 JSON/checkpoints/dump 和首个 unexpected 时间戳；Windows Chaos 读取 `release-smoke-waits.txt` 的线程/async/syncblk，区分物理I/O未退出与逻辑完成；ABI 从 test.log 找到首个 UID，再回到当轮 retirement/ALC 根引用。手动 Release Gate 的 Windows Chaos 当前可尝试3次；即使 job 最终绿，先前失败也必须解释，不可视作已修复。

## 7. 安全、文档与管理员前置

### 安全（M；接受风险/管理员变更为 H）

```bash
dotnet list Sharplink.slnx package --include-transitive --vulnerable --format json --no-restore \
  > "$EVIDENCE/vulnerabilities.json"
dotnet list Sharplink.slnx package --include-transitive --deprecated --format json --no-restore \
  > "$EVIDENCE/deprecations.json"
gh api 'repos/SunSi12138/SharpLink/code-scanning/alerts?state=open' > "$EVIDENCE/code-scanning-open.json"
gh api 'repos/SunSi12138/SharpLink/dependabot/alerts?state=open' > "$EVIDENCE/dependabot-open.json"
gh api repos/SunSi12138/SharpLink/private-vulnerability-reporting
gh api repos/SunSi12138/SharpLink/rules/branches/main
gh api repos/SunSi12138/SharpLink/environments/release
```

必须核对扫描完整覆盖生产与测试/工具还原项目，源请求无错误；高危或运行时可达中危阻断，弃用项逐项处置或明确迁移计划。CodeQL 分析须来自最终 main SHA；默认分支旧结果不能代替。实际读到：open CodeQL=0、Dependabot=0；private reporting enabled；`release` environment 存在、`NUGET_USER` secret 名称存在；required reviewers为空。main 由 active ruleset保护，仅允许merge PR、要求 release-summary、解决讨论；required approving review count=0，因此每PR独立子代理审核需流程主动落实，GitHub不会自动强制。

NuGet Trusted Publishing policy 的 owner/repo/workflow=`release-gate.yml`/environment=`release` 与九包 owner 一致性本次无法从代码证实，须管理员在 NuGet 页面确认；不要读取 secret 值。管理员若要增设环境审批需明确授权，不能将缺失审批规则冒充已配置。失败下一步：安全报告定位具体 advisory→依赖链→运行可达性；OIDC 配置失败核对 policy exact字段/包 owner/环境，不改用长期token。

### 文档迁移（M，范围/重大兼容取舍 H）

最终检查 `CHANGELOG.md`、README 包引用/Quick Start、`doc/migration.md`、`doc/unsafe-blit-3.0-migration.md`、`doc/rpc-channel-3.0-migration.md`、`doc/3.0-builder-logger-migration.md`、`doc/rpc-wire-identity.md`、`doc/codec-compatibility.md`、`doc/releasing.md`，以及 #933 合入后的 ADR0003/0004 和 maintenance 使用说明。

要求：所有例子3.0.0且实际包可编译；公开移除/替代、Generator重建/API5 identity、wire minor4与可选能力、平台实验状态、OneWay语义、maintenance限制准确一致。不能写“Binding/receipt完整交付”“零性能影响”“无需重建旧生成程序集”或借历史性能页替代本次数据。文档例子失败先在空缓存 Quick Start/对应consumer定位签名与包差异，修正文档和实际入口一致后重验。

## 8. dev→main、最终 main SHA 重验、tag 与 NuGet

1. **M** — 所有预冻结决策与门禁满足后，创建 dev→main Release PR，release notes 附精确证据索引、范围、迁移、实验平台和全部未决项。独立子代理审核完整合并差异及冲突，不以各功能PR已审替代发布整合审核。核对明确承诺进入本次发布的功能，不得遗漏或静默降级；保留长期跟踪 issue 本身不等于发布失败。
2. **M** — PR 的 Release Gate 自动执行。核对测试实际 checkout SHA（PR可能为merge SHA），不要把PR head名字误作工件身份。确认预定main合并内容与证据对应，并完成受审head合并。
3. **M** — 合并后立即取最终 `main` 完整 SHA，冻结 main 不再插入依赖更新。**即便内容类似，只要SHA变化，重做最终 main 的构建/测试、实包/API、平台/Trim/AOT、完整性能和recorder/allocation、10m soak/ABI、安全/文档绑定。** 运行 `release-gate.yml --ref main` 与 `nightly.yml --ref main -f release_soak=true`，核对每个 run headSha；异SHA旧工件仅作历史对照。
4. **M/H** — 将最终main门禁表逐行标为已通过/阻断，列所有 run ID和产物；原目标完整达成且无未接受取舍才批准tag。管理员核实 NuGet policy、保护和发布权限。要求用户接受例外时明确失败事实和影响，不用“是否继续”掩盖改变合同。
5. **M（仅在已有真实发布授权内）** — 从最终SHA创建一次性正式tag，不移动/重打：

   ```bash
   git fetch origin main
   FINAL_MAIN=$(git rev-parse origin/main)
   test "$FINAL_MAIN" = "$ACCEPTED_MAIN_SHA"
   git tag -a v3.0.0 "$FINAL_MAIN" -m 'SharpLink 3.0.0'
   git push origin refs/tags/v3.0.0
   ```

   之前先确认本地和远端不存在同名tag及NuGet对应版本。tag push会重新运行Release Gate，只有同次pack工件全部门禁通过，才进入 `publish-nuget`。`workflow_dispatch` 只验证、不会publish。禁止人为下载另一个run的包代替。
6. **M/H** — `publish-nuget` 使用OIDC，在release环境按 Abstractions→GenerationControl→Runtime→Sdk→Serializer.SharpPack→Client→Compression.Zstd→Server→Hosting 发布；匹配snupkg由 NuGet push 携带。跟踪每包成功/失败，不能仅看到job success就结束。若中途失败，先查询NuGet实际已存在包并下载核对版本/commit/内容身份，再决定安全续发；`--skip-duplicate`不证明已有包内容正确，不能盲目重跑发布。
7. **M** — 等NuGet可索引后，从公开源新空缓存安装九包消费路径、确认symbol状态与实际repository commit，GitHub Release指向同一tag/SHA，notes与Changelog一致；只有实际存在且可消费才说已发布。创建Release正文用审核好的notes文件，稳定3.0不得标prerelease。
8. **M/H** — 若已发布内容有缺陷，停止推广并记录受影响包；管理员执行deprecate（给替代版本），从已发布SHA做最小修复并走新补丁版本完整门禁。不可覆盖既有NuGet版本或移动tag掩盖错误；安全事件按 `SECURITY.md`。

## 9. 交给下一位执行者的最短起步

1. 读 root 最新总交接与 progress.json；查询 #931/#933/#754、工具PR的新head，不重跑旧失败。
2. 将本手册当前“待收口”事项放到计划顶部：#775原范围决策/实现、真正双版本wire证据、full矩阵分片调度/资源与 #954 工具验收、三OS packaged trim wiring（缺失项需各自受审后使用）。
3. 沿 #931→#933、#732专项→整合→最终台账顺序推进；现有owner保留原始数据和诊断职责。
4. 冻结后按门禁跑；任何产品修复导致SHA改变，都重建对应最终发布证据。记录完成状态和下一诊断动作，不提前宣称ready。
