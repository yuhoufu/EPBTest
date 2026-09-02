# MTTFTest 简易打包与更新说明

## 1. 一键打包

在仓库根目录运行：

```powershell
.\Tools\New-FormalRelease7z.ps1
```

流程与 Visual Studio 的 Release 重建接近：

```text
MTTfTest.csproj Release Rebuild
    → MTTfTest\bin\Release
    → 7-Zip 压缩
    → 7z 完整性测试
    → SHA-256
```

不再经过受控 Release 中转、Git 干净门禁、完整自动化回归或二次 QuickDeploy 封包。

## 2. 成功判断

只有控制台最后明确显示以下文字才算成功：

```text
=== 打包成功 ===
```

默认输出目录：

```text
artifacts\deploy
```

每次生成三个文件：

```text
MTTFTest_V<版本>_<时间>.7z
MTTFTest_V<版本>_<时间>.7z.sha256
MTTFTest_V<版本>_<时间>.7z.result.json
```

脚本自动从新编译的 `MTTFTest.exe` 读取版本号，不需要手工传版本参数。

## 3. 脚本实际检查

简易打包仅保留交付所需的基本检查：

1. 自动查找 Visual Studio MSBuild；
2. 自动查找 `7z.exe`；
3. 对主项目执行 `Release`、`AnyCPU` 重建，项目自身目标平台仍为 x86；
4. 确认主程序、配置、Controller 和 Watchdog/Agent 文件存在；
5. 使用 7-Zip 压缩 `MTTfTest\bin\Release`；
6. 执行 `7z t` 完整性测试；
7. 生成 SHA-256 和结果摘要。

任何步骤失败时显示：

```text
=== 打包失败 ===
```

并返回退出码 `1`。

## 4. 可选参数

指定输出目录：

```powershell
.\Tools\New-FormalRelease7z.ps1 -OutputRoot 'D:\MTTFTest-Packages'
```

指定 MSBuild：

```powershell
.\Tools\New-FormalRelease7z.ps1 `
    -MsBuild 'D:\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe'
```

指定 7-Zip：

```powershell
.\Tools\New-FormalRelease7z.ps1 `
    -SevenZip 'C:\Program Files\7-Zip\7z.exe'
```

## 5. Git 状态

工作区有未提交改动时不再拒绝打包。脚本仍会在结果中记录 `gitCommit` 和 `gitDirty`，并显示警告，是否交付由操作人员判断。

正式交付前仍建议先提交代码并确认：

```powershell
git status --short
```

## 6. 交付与解压

至少成对交付：

```text
<包名>.7z
<包名>.7z.sha256
```

在目标电脑本地完整解压，不要直接从压缩包预览、邮件附件或网络共享中运行。

更新现场程序前：

1. 安全停止正在运行的试验；
2. 等待 DO、AO、电源处置和数据落盘完成；
3. 完全退出旧程序；
4. 解压新包并运行其中的 `MTTFTest.exe`。

## 7. 常见失败

### 找不到 MSBuild

安装 Visual Studio 或 Build Tools，或者使用 `-MsBuild` 指定路径。

### 找不到 7-Zip

安装 7-Zip，或者使用 `-SevenZip` 指定 `7z.exe`。

### Release 重建失败

按控制台最前面的编译错误处理。常见原因是 NI、CAN 或其他供应商程序集缺失，以及程序正在占用输出文件。

### Release 输出不完整

说明项目虽然编译结束，但主程序依赖或 Watchdog/Agent 没有复制齐全。不要手工拼接旧 DLL，应先修复项目构建。

### 7-Zip 完整性测试失败

该压缩包不可交付，修复磁盘空间或 7-Zip 环境后重新生成。

## 8. 严格发布流程

原有 `Build-Release.ps1`、`Verify-Release.ps1` 和 `New-QuickDeployBundle.ps1` 仍保留，供需要完整自动化回归、构建身份和严格审计时手工使用。普通 VS 风格打包不再调用这些脚本。
