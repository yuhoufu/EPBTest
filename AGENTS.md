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

The first command is the normal local build. The remaining commands validate release compilation, control/watchdog behavior, disk persistence, and the .NET 8 debugger. Use `Tools\Build-Release.ps1` only for a clean, validated field package with vendor SDKs available.

> 中文提示：Debug 固定使用 x86；现场发布前必须确认工作区干净且硬件依赖齐全。

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

Place documents, scripts, and other artifacts produced while Codex executes tasks in the repository-root `Codex/` directory. This directory is local-only and Git-ignored. Do not use `C:/Users/19812/Documents/Codex/` for these artifacts.

> 中文提示：Codex 执行任务过程中产生的文档、脚本及其他产物统一放在项目根目录的 `Codex/` 文件夹中，并保持 Git 忽略；不得放在 `C:/Users/19812/Documents/Codex/`。

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
