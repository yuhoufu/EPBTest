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
