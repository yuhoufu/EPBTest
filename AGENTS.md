# Repository Guidelines

## Project Structure & Module Organization

`TfTest.sln` is the main Windows solution. `MTTfTest/` contains the .NET Framework 4.8 WinForms application. Control orchestration belongs in `Controller/`, persistence in `DataOperation/`, models and configuration loading in `Config/`, and NI hardware access in `IO.NI/`. Supporting libraries include `Timing/`, `Utils/`, `ZlgCanComm/`, `PowerSupply.Core/`, and the watchdog projects. Tests live under `Tests/`; scripts are in `Tools/`, deployable XML files in `MTTfTest/Config/`, and engineering notes in `docs/` and `开发日志/`. The presence of the legacy `ZlgCanComm/` library does not mean the current program uses CAN hardware.

> 中文提示：`旧代码备份/`、运行日志、`bin/`、`obj/` 和发布产物不属于源代码。

## Build, Test, and Development Commands

Run commands from a Visual Studio Developer PowerShell on Windows:

```powershell
msbuild TfTest.sln /p:Configuration=Debug /p:Platform=x86
msbuild TfTest.sln /p:Configuration=Release /p:Platform="Any CPU"
.\Tests\AdaptiveControlTests\bin\Debug\AdaptiveControlTests.exe
.\Tests\EpbDiskWriterTests\bin\Debug\EpbDiskWriterTests.exe
dotnet test .\Tests\PowerSupplyDebugger.Tests\PowerSupplyDebugger.Tests.csproj
```

The first command is the normal local build. The remaining commands validate release compilation, control/watchdog behavior, disk persistence, and the .NET 8 debugger. Use `Tools\Build-Release.ps1` for a clean release candidate with the required vendor SDKs available; field deployment requires separate acceptance evidence.

> 中文提示：Debug 固定使用 x86；现场发布前必须确认工作区干净且硬件依赖齐全。

## Release Packages and Packaging

### 当前最新完整程序包（2026-10-09 核对）

产品版本统一读取 `ProductVersion.props` 的 `EpbProductVersion`，当前为 **4.1.0.9**。现存完整包由干净提交 `4fa2d27deebb5368d6600222a8ab647c66ca6a06` 构建及组包；后续 `main` 合并不会自动重建该包，不能将它标记为由当前 `main` HEAD 构建。

| 用途 | 绝对路径 |
| --- | --- |
| 最终交付根目录 | `D:\Github\wanxiang\EPBTest_Releases\V4.1.0.9\` |
| 完整解压目录 | `D:\Github\wanxiang\EPBTest_Releases\V4.1.0.9\V4.1.0.9_4fa2d27deebb_AUTO_RECOVERY_ONECLICK\` |
| 分发压缩包 | `D:\Github\wanxiang\EPBTest_Releases\V4.1.0.9\V4.1.0.9_4fa2d27deebb_AUTO_RECOVERY_ONECLICK.7z` |
| 压缩包摘要 | 同一压缩包路径追加 `.sha256.txt` |
| 交付与验收说明 | `D:\Github\wanxiang\EPBTest_Releases\V4.1.0.9\V4.1.0.9_发布与验收说明.md` |
| 本次基础候选包 | `D:\Github\wanxiang\EPBTest\Codex\wj-fixes-20261008\release-base-4fa2d27\V4.1.0.9-4fa2d27deebb-20261008_090700\` |
| 本次构建、组包和核验记录 | `D:\Github\wanxiang\EPBTest\Codex\wj-fixes-20261008\` |

压缩包大小为 `34,572,728` 字节，SHA-256 为 `A27576DEE4E70621F07CF32A429C7BDC0BE51278CFBF072BF4F4CD8A65B9C122`。基础身份 `Base/build-identity.json` 与完整清单 `automatic-bundle.json` 均为 `CANDIDATE_NOT_FIELD_VALIDATED`，对应 `deploymentApproved=false` / `fieldDeploymentApproved=false`。候选标识与 Git 标签为 `v4.1.0.9-rc.1`，标签指向上述实际构建提交；组包脚本本身不会创建 Git tag。入口名“一键安装正式版.cmd”不代表已完成现场验收。

### 后续打包路径与执行顺序

1. **固定存放位置。** 最终交付按版本保存到 `D:\Github\wanxiang\EPBTest_Releases\V<版本>\`，目录名由脚本生成 `V<版本>_<源码提交前12位>_AUTO_RECOVERY_ONECLICK`，旁边保存 `.7z`、`.7z.sha256.txt` 和交付说明；保留历史版本，不覆盖已封包内容。基础候选、日志、回归及核验证据放到当前仓库 `Codex/<本次任务>/`，这是最终交付目录与本地产物目录的明确分工。发布产物不得提交 Git。
2. **冻结源码。** 从当前主工作区 `D:\Github\wanxiang\EPBTest` 使用 **PowerShell 7** 打包；先提交改动并确认工作区干净，记录完整 HEAD。构建到组包结束期间不得编辑、切换分支或提交。检查 MSBuild、.NET Framework 4.8、.NET SDK / .NET 8 运行时、Python `py -3`、7-Zip 和项目实际需要的厂商 SDK；硬件范围遵守下文 `Current Hardware Scope`。`EPB_TEST_ARTIFACT_ROOT` 使用 `Codex/` 下的 Windows 绝对路径，`TEMP` / `TMP` 使用短路径 `D:\Github\wanxiang\EPBTest\Codex\t`，避免旧框架的路径长度限制。
3. **构建基础候选。** 执行 `Tools/Build-Release.ps1 -Candidate -PackageRoot <Codex下的本次基础候选根>`，必须显式指定 `-PackageRoot`；脚本默认的 `artifacts/releases` 不作为本项目交付位置。脚本还原依赖、清理 `MTTfTest/bin/Release` 暂存输出、以 Release / `Any CPU` 重建解决方案（主程序包身份为 x86），运行控制、落盘、电源及安装/维护脚本回归，核验组件版本和 8 份 XML 配置，生成 `build-identity.json`、`SHA256SUMS.txt`。通过后输出不可变目录 `V<版本>-<提交前12位>-<UTC时间戳>`。不要直接分发或部署 `bin/Release`，不要使用 `-AllowDirtyCandidate` 交付。
4. **独立核验并补测。** 对上一步实际输出的 `PackageOutput` 执行 `Tools/Verify-Release.ps1 -ReleaseDirectory <基础候选目录>`；它核对身份、文件集合、大小、摘要及配置。再运行 `Tests/IndependentRecovery.ProcessTests/bin/Release/IndependentRecovery.ProcessTests.exe` 全套；Build-Release 没有运行该完整进程套件。每步必须确认退出码和通过摘要，失败即停止并保留证据。
5. **组装完整一键包。** 再确认同一干净 HEAD、基础身份中的 `gitCommit` / `gitDirty=false`、版本及候选状态，再执行 `Tools/New-AutomaticRecoveryBundle.ps1 -ReleaseDirectory <基础候选目录> -OutputRoot <最终版本交付目录> -CandidateTag v<版本>-rc.<候选序号>`，不传 `-LegacyRecovery`。脚本复制完整 `Base/`，加入本次 Release 构建的 `FallbackGuard/`（含 SQLite x86 依赖）、`Tools/`、13 个中文 CMD 入口和说明，写入 `automatic-bundle.json`，使用 7-Zip `a -t7z -m0=LZMA2 -mx=9 -mmt=2` 压缩，执行 `7z t` 后生成 SHA-256 文件。脚本拒绝覆盖同名包，但不自行保证组包源码干净或 `packagingGitCommit == gitCommit`，必须额外核对。

同版本的新构建使用递增候选序号，不移动或覆盖已有标签；已有 `v4.1.0.9-rc.1` 指向现存包，因此从当前 `main` 再打 V4.1.0.9 包时应使用未占用的 `v4.1.0.9-rc.2` 或后续序号。

命令参数示例（在 PowerShell 7 中分步执行；每次使用新的 `$baseRoot`，`$releaseDirectory` 取 Build-Release 日志中的实际 `PackageOutput`，不要复用历史目录；示例候选序号按 V4.1.0.9 下一构建选择，其他版本按实际已有标签调整）：

```powershell
Set-Location -LiteralPath 'D:\Github\wanxiang\EPBTest'
[xml]$versionProps = [IO.File]::ReadAllText((Join-Path (Get-Location) 'ProductVersion.props'))
$version = [string]$versionProps.Project.PropertyGroup.EpbProductVersion
$candidateTag = 'v' + $version + '-rc.2'
$releaseWork = Join-Path 'D:\Github\wanxiang\EPBTest\Codex' ('release-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$baseRoot = Join-Path $releaseWork 'release-base'
$deliveryRoot = Join-Path 'D:\Github\wanxiang\EPBTest_Releases' ('V' + $version)
$env:EPB_TEST_ARTIFACT_ROOT = [IO.Path]::GetFullPath((Join-Path $releaseWork 'tests'))
$env:TEMP = 'D:\Github\wanxiang\EPBTest\Codex\t'
$env:TMP = $env:TEMP
[IO.Directory]::CreateDirectory($env:EPB_TEST_ARTIFACT_ROOT) | Out-Null
[IO.Directory]::CreateDirectory($env:TEMP) | Out-Null
pwsh -NoProfile -File .\Tools\Build-Release.ps1 -Candidate -PackageRoot $baseRoot
# 确认成功后设置 $releaseDirectory 为本次 PackageOutput，再逐步执行：
pwsh -NoProfile -File .\Tools\Verify-Release.ps1 -ReleaseDirectory $releaseDirectory
.\Tests\IndependentRecovery.ProcessTests\bin\Release\IndependentRecovery.ProcessTests.exe
pwsh -NoProfile -File .\Tools\New-AutomaticRecoveryBundle.ps1 -ReleaseDirectory $releaseDirectory -OutputRoot $deliveryRoot -CandidateTag $candidateTag
```

**封包核验与放行边界：** 必须核对 Base 与完整清单的版本、同一构建提交、干净状态和候选状态，以及九类产品组件的十份文件版本（Protocol 在 Base/FallbackGuard 各一份，摘要必须相同）；核对实际文件集合与清单、全部 SHA-256、13 个入口、`7z t` 及归档摘要。对完整包的 `RecoveryGuard-Acceptance.ps1` 在 PowerShell 5.1 / 7 下做离线包核验，不传 `-InstallRoot`，证据用 `-OutputPath` 保存到 `Codex/`。包完整性通过不证明硬件动作、计数推进、正式落盘或长期运行通过；`NOT_VERIFIED` 不得写为通过。缺少现场验收时始终使用 `-Candidate`，不能靠省略开关使基础包显示 `FORMAL_RELEASE` 来代替验收，也不能手改清单放行。组包不自动安装、升级或部署现场。需要修改包内文件时重新构建/组包；补充交付说明保存到包外的版本目录。

本版本修复与验证依据见 `docs/02_Issues/2026-10-08_V4.1.0.9_项目切换与声光退出修复.md`。新包交付后，同步更新本节“当前最新完整程序包”的版本、路径、实际构建提交、摘要和验收状态。

## Coding Style & Naming Conventions

Follow existing C# style: four-space indentation, braces on new lines, `PascalCase` for types and public members, `camelCase` for parameters and locals, and `_camelCase` for private fields. End asynchronous method names with `Async`. Preserve cancellation paths and avoid blocking UI or acquisition threads. No repository-wide formatter is configured; match the surrounding file and do not hand-edit generated `*.Designer.cs` code.

## Testing Guidelines

Add regression coverage beside the affected subsystem. Console harnesses use `*Tests.cs`, `RunAll()`, and explicit assertions; xUnit uses `[Fact]`/`[Theory]` with behavior-focused names. No numeric coverage threshold is enforced. Run all three suites for shared controller, persistence, watchdog, or power-supply changes.

> 中文提示：NI、液压、电源或其他实际在用现场硬件测试若无法本地执行，必须在 PR 中明确说明并补充现场验证记录。

## Current Hardware Scope

The current program does not contain or depend on any CAN hardware. Do not probe, require, validate, or diagnose CAN adapters, CAN drivers, CAN buses, or CAN connectivity for this project. CAN state must never be used as a deployment prerequisite, acceptance item, recovery condition, or blocker. Legacy source folders, DLLs, configuration remnants, or Windows devices related to CAN do not change this rule unless the user explicitly changes the current hardware scope.

> 中文提示：当前程序不包含任何 CAN 硬件。后续分析、远程检查、部署和验收不得检测 CAN，不得把 CAN 设备或驱动状态作为前置条件、验收项或阻塞原因；仓库中遗留的 `ZlgCanComm` 等代码不代表当前程序使用 CAN。

## Documentation Placement

Unless specified otherwise, create documents as Markdown (`.md`). With no requested path, choose `docs/` or a suitable subdirectory based on purpose. Use descriptive filenames; add dates or versions to incident and validation records. Keep screenshots in an adjacent `截图/` folder and use relative links.

> 中文提示：未指定格式时默认生成 Markdown；未指定路径时，应根据文档的生产意图和作用落到 `docs/` 或其合适的子目录，不要默认放在仓库根目录。

## Codex Local Artifacts

Place documents, scripts, and other artifacts produced while Codex executes tasks in the repository-root `Codex/` directory, except for final release deliverables in the location specified by `Release Packages and Packaging`. This directory is local-only and Git-ignored. Do not use `C:/Users/19812/Documents/Codex/` for these artifacts.

> 中文提示：Codex 执行任务过程中产生的文档、脚本及其他产物统一放在项目根目录的 `Codex/` 文件夹中，并保持 Git 忽略；最终发布交付物按上文发布目录规则存放。不得放在 `C:/Users/19812/Documents/Codex/`。

## Commit & Pull Request Guidelines

History follows Conventional Commit-style subjects. Keep type keywords such as `feat`, `fix`, `test`, `docs`, and `chore`, plus an optional subsystem scope, in their conventional form; write the remaining commit description in Chinese. Examples: `fix(watchdog): 修复恢复拉起失败链路` and `docs: 补充现场验收记录`. Keep commits focused. Pull requests should explain the goal, safety or data-integrity impact, configuration changes, and verification commands. Link issues or incident documents; include UI screenshots and hardware-dependent field evidence. Never commit credentials, site secrets, large runtime logs, or build outputs.

> 中文提示：所有提交提示语除类型关键词、可选作用域和必要的代码标识外，均使用中文；不要使用纯英文提交说明。

## Agent Tooling

When a required Python package is missing, install it without prompting through the Tsinghua mirror: `python -m pip install <package> -i https://pypi.tuna.tsinghua.edu.cn/simple`（缺少依赖时直接安装，无需询问）.

## Remote Diagnostics: Mandatory Resource and Session Limits

以下规则适用于本项目所有 Codex 任务、子代理和生成的远程检查脚本，尤其是正在运行试验的 wj-epb。**只读诊断也必须控制资源占用，不能以“没有修改文件或配置”推断对现场无影响。**

事故依据：2026-09-12，Codex 状态检查将 `Get-Content` 返回的日志对象送入 `ConvertTo-Json -Depth 35`。现场 Windows PowerShell 5.1 展开了字符串附带的 Provider/类型/程序集元数据；本地中断后远端会话仍残留，`wsmprovhost.exe` 占用约 21 GiB，导致试验恢复、看门狗接管及备份均遭遇内存不足。后续必须遵守以下约束，不得重新在现场执行该问题命令。

### Plain Data Only

- 首次连接先确认**远端** PowerShell 版本；本地使用 PowerShell 7 不代表默认 WinRM 端点也使用 PowerShell 7。默认按 Windows PowerShell 5.1 的行为设计兼容检查。
- 禁止将带扩展属性的 `Get-Content` 输出或原始 `Process`、`FileInfo`、CIM、任务、Provider、Type、Assembly 等复杂对象直接送入深层 `ConvertTo-Json`。禁止用 `Select-Object *` 或不断增加 `-Depth` 来解决诊断输出问题。
- 日志必须通过有界文本读取取得纯字符串，例如 `StreamReader` 配合固定容量尾部队列；小文件可以使用有大小上限的 `[IO.File]::ReadAllText()` 后解析 JSON。不得为取末尾几行而整文件读入内存，不得把 `Get-Content -Raw` 当作去除扩展属性的保证。
- 返回数据必须显式构造简单字段：字符串、布尔值、数值，以及只包含这些值的有界数组/对象；时间显式转为 ISO 8601 字符串。对所选字段的值也要检查类型，不能只靠 `Select-Object` 假定安全。日志和元数据分开返回。
- JSON 深度默认不超过 6；确需更深时，先在本地用相同远端 PowerShell 主版本验证纯数据结构、输出大小和资源上界。对 Provider/反射对象禁止深层展开，`-Compress` 不能代替这一约束。

### Bounded Collection and Independent Monitoring

- 轻量状态/日志检查默认上限：单次远程调用 30 秒、返回内容 1 MiB、每个日志最多 200 行且每行最多 4096 字符、本任务新建远端诊断宿主私有内存 256 MiB。截断必须标注，不能把不完整结果当完整证据。大日志、备份和长任务应使用独立的流式传输/执行流程，事先明确自己的时间、内存和文件大小预算，不能沿用无限输出的交互调用。
- 超时和资源监控必须由调用方或独立监督路径执行；不能只在可能卡死的序列化代码内检查。可能发生对象展开的命令先在本地隔离子进程验证，并设置强制超时和内存上限；不得为了复现而在现场耗尽内存。
- 开始前、运行中及结束后检查系统物理内存、提交额度/可用虚拟内存和本任务远端进程的 Private Bytes。不能仅凭 Working Set、物理空闲或分页文件使用率认定资源充足。检测到资源快速增长、超限或系统内存不足时，立即停止本任务的诊断负载并清理它创建的会话，再复核资源是否释放。
- 大文件通过有大小、时间和重试上限的文件传输或归档回传；分析尽量在本地进行。禁止将文件字节数组、长日志数组或复杂运行时对象序列化成巨量 JSON，经远程控制通道回传。

### Session Ownership and Verified Cleanup

- 每次新建会话后，在执行实际诊断前记录任务标识、目标主机、ShellId/InstanceId、远端宿主 PID 与启动时间、来源地址、创建时间；记录落在仓库 `Codex/` 下。优先使用任务可识别的会话名称。不得用工具的执行会话编号冒充 Windows PID。
- 正常完成、失败、超时、取消都必须走 `try/finally` 清理。**本地 Ctrl+C、工具结束、连接断开、finally 写在代码里，均不等于远端资源已经释放。**
- 中断或清理返回异常后，用新的短时控制连接按已记录的精确 ShellId 查询并关闭**本任务创建**的残留会话；必要时使用 `Remove-WSManInstance` 精确删除该 Shell。此类清理属于已授权任务的必要收尾，无需再次询问用户。
- 清理后必须确认目标 Shell 不存在、对应宿主已经退出或本任务占用确已释放，并记录系统内存复核结果。若必须终止残留进程，先重新核对主机、PID、启动时间及任务归属，防止 PID 重用。用于清理的临时连接也必须回收。
- 禁止按 `wsmprovhost` 名称批量结束进程、关闭所有远程会话，或通过重启 WinRM/现场主机来代替精确清理。不得清理用户或其他任务的会话；归属不明时先保留证据并说明，已有用户明确授权的指定会话除外。
- 报告必须分别交代诊断结果、诊断资源/会话是否清理、现场业务是否继续运行。不得以“当前心跳正常”掩盖未收尾的诊断进程，也不得将内存释放或备份成功写成试验自恢复成功。
