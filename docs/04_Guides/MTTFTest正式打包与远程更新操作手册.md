# MTTFTest 正式打包与远程更新操作手册

## 1. 适用范围

本文用于把开发电脑中的 MTTFTest 源码制作成可交付的正式包，并在远程实际机上完成安装或升级。

当前正式流程为：

```text
提交并清理源码
    → New-FormalRelease7z.ps1 一键构建、校验并制作操作员包
    → artifacts\deploy 下的 .7z 和 .sha256
    → 复制到远程机本地磁盘并完整解压
    → 双击新包第一层 MTTFTest.exe
    → 接受一次 UAC，自动更新 Current、服务、任务和快捷方式
    → 以后从桌面快捷方式以普通权限运行
```

正式发布流程已经取消强制 600 秒持久化耐久测试。完整功能回归、写盘回归、电源测试、现场门禁测试和部署契约测试仍会执行，因此打包仍需等待数分钟，具体时间取决于电脑性能。

## 2. 明确禁止的发布方式

以下方式不能用于实际机：

- 直接复制 `MTTfTest\bin\Debug`；
- 直接复制 `MTTfTest\bin\Release`；
- 只复制 `MTTFTest.exe`；
- 手工把 DLL 或 EXE 覆盖到正在运行的 `Current`；
- 从压缩包内部或网络共享目录直接运行安装程序；
- 使用 `-AllowDirtyCandidate` 生成的 `DIRTY` 包投入生产；
- 用新包的 `Config` 覆盖 `C:\ProgramData\MTTFTest\Config` 中的现场配置。

`bin\Release` 只是构建暂存区。只有 `artifacts\releases` 中由脚本完成验证的独立目录，才可以继续制作快捷部署包。

## 3. 开发电脑前置条件

### 3.1 软件要求

- Visual Studio 2022 或包含 MSBuild 的 Build Tools；
- .NET Framework 4.8 开发组件；
- .NET 8 SDK；
- Windows PowerShell；
- Python 3，并且 `py.exe` 可用；
- 7-Zip，用于制作最终 `.7z`；
- 工程编译需要的 NI、CAN 等供应商 SDK/程序集。

自动化测试不要求连接真实程控电源，但编译依赖必须齐全。

### 3.2 确认当前目录

打开“Developer PowerShell for VS 2022”，进入仓库根目录：

```powershell
Set-Location 'D:\Github\wanxiang\EPBTest'
```

确认当前目录正确：

```powershell
Get-Location
Test-Path '.\TfTest.sln'
```

第二条命令应返回：

```text
True
```

### 3.3 确认版本号

当前主版本号从 `MTTfTest\MTTfTest.csproj` 的 `ApplicationVersion` 读取：

```powershell
Select-String -LiteralPath '.\MTTfTest\MTTfTest.csproj' `
    -Pattern '<ApplicationVersion>'
```

当前代码应显示类似：

```xml
<ApplicationVersion>2.14.2.3</ApplicationVersion>
```

每次需要让实际机替换为新版本时，新包程序集版本必须高于实际机上的版本。如果版本相同或更低，健康的已安装版本会被保留，不会重复替换 `Current`。

版本号同时存在于主程序、Controller、Watchdog、SessionAgent、SafetyAgent、SafetyHardware 和协议程序集。不要只改一个文件；Release 脚本会检查主要组件版本是否一致并在不一致时拒绝发布。

### 3.4 确认没有运行中的构建产物

关闭以下程序：

- VS 中正在调试的 `MTTFTest.exe`；
- `AdaptiveControlTests.exe`；
- `EpbDiskWriterTests.exe`；
- 正在占用 `bin\Release` 的其他测试或工具。

否则 MSBuild 可能因为文件被锁定而失败。

## 4. 正式封包前的 Git 检查

### 4.1 检查变更

```powershell
git status --short
git diff --check
git diff
```

确认：

- 没有误加入运行日志、`bin`、`obj` 或现场数据；
- 没有包含账号、密码、密钥等敏感信息；
- `git diff --check` 没有空白错误；
- 本次修改内容与准备发布的版本一致。

### 4.2 提交本次修改

只暂存本次确认过的文件，不建议不加检查地执行 `git add .`：

```powershell
git add <本次修改的文件>
git commit -m "fix: 本次发布内容的中文说明"
```

再次检查：

```powershell
git status --short
```

正式封包时必须没有任何输出。如果工作区不干净，`Build-Release.ps1` 会立即拒绝生成正式包。

## 5. 生成受控 Release 目录

### 5.1 自动查找 MSBuild

不同电脑的 Visual Studio 安装路径可能不同，建议先执行：

```powershell
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * `
    -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\MSBuild.exe'

$msbuild
```

应输出一个实际存在的 `MSBuild.exe` 路径。继续确认：

```powershell
Test-Path -LiteralPath $msbuild
```

应返回 `True`。

### 5.2 一键生成 7z（推荐）

```powershell
.\Tools\New-FormalRelease7z.ps1
```

脚本会自动查找 MSBuild、构建 Release、运行全部发布校验、创建并回读测试 `.7z`、复核外层 SHA-256，并在成功时输出 `.7z`、`.sha256` 与 `.result.json` 的完整路径。任何阶段失败均返回退出码 `1`，未显示“交付成功”不得交付。

可使用 `-OutputRoot` 指定交付输出根目录：

```powershell
.\Tools\New-FormalRelease7z.ps1 -OutputRoot 'D:\MTTFTest-Deliveries'
```

### 5.3 手动执行正式构建

```powershell
.\Tools\Build-Release.ps1 -MsBuild $msbuild
```

如果脚本默认配置的 MSBuild 路径在本机有效，也可以直接执行：

```powershell
.\Tools\Build-Release.ps1
```

不要再传递 `-PersistenceSoakSeconds`，该参数已经移除。

### 5.3 脚本会自动完成的工作

脚本会依次完成：

1. 固定 Git commit、分支、源码状态和内容指纹；
2. 检查关键安全源码是否真正加入旧式 `.csproj` 编译清单；
3. 检查 Watchdog/SafetyAgent 协议版本和依赖边界；
4. 检查 8 个默认 XML 配置模板是否完整且无多余项；
5. 还原 NuGet 和 .NET 运行时资产；
6. 清空本地 `MTTfTest\bin\Release` 暂存目录；
7. 执行正式 Release 全解决方案重建；
8. 检查主程序、Watchdog、SessionAgent、SafetyAgent 和协议 DLL 是否齐全；
9. 检查主要组件版本号一致；
10. 运行 AdaptiveControlTests；
11. 运行 EpbDiskWriterTests；
12. 运行 PowerSupplyDebugger.Tests；
13. 运行 Python 现场门禁测试；
14. 运行部署脚本和快捷部署命令解析测试；
15. 再次确认测试期间源码没有发生变化；
16. 校验构建前后配置哈希一致；
17. 生成 `build-identity.json` 和 `SHA256SUMS.txt`；
18. 在独立暂存目录验证，通过后生成不可覆盖的 Release 目录。

任一步骤失败都会中止，不应从失败后的 `bin\Release` 手工收集文件。

### 5.4 判断是否构建成功

成功时末尾会显示类似：

```text
Release 正式包已生成并独立校验：D:\Github\wanxiang\EPBTest\artifacts\releases\V2.14.2.3-xxxxxxxxxxxx-yyyyMMdd_HHmmss
ScratchOutput=...
PackageOutput=D:\Github\wanxiang\EPBTest\artifacts\releases\V2.14.2.3-xxxxxxxxxxxx-yyyyMMdd_HHmmss
Commit=...
Branch=...
Dirty=False
ConfigSha256=...
BuildUtc=...
```

记录 `PackageOutput=` 后面的完整目录。不要把 `ScratchOutput=` 指向的 `bin\Release` 当作现场包。

## 6. 再次验证 Release 目录

正常情况下 Build-Release 已经执行过验证。如需交付前再次核对：

```powershell
$releaseDirectory = '<PackageOutput 显示的完整目录>'
.\Tools\Verify-Release.ps1 -ReleaseDirectory $releaseDirectory
```

确认目录中至少包含：

```text
MTTFTest.exe
MTTFTest.Watchdog.exe
MTTFTest.SessionAgent.exe
MTTFTest.SafetyAgent.exe
MTTFTest.Watchdog.Protocol.dll
Config\
Deployment\
build-identity.json
SHA256SUMS.txt
MTTFTest.UnattendedMode.required
```

## 7. 手动制作操作员快捷部署包

### 7.1 生成部署包

继续使用上一步的明确 Release 路径：

```powershell
$releaseDirectory = '<PackageOutput 显示的完整目录>'
.\Tools\New-QuickDeployBundle.ps1 `
    -ReleaseDirectory $releaseDirectory
```

不要通过“取最新目录”的模糊脚本自动选择，以免误选到旧候选包。

### 7.2 成功输出

默认输出目录：

```text
D:\Github\wanxiang\EPBTest\artifacts\deploy
```

生成文件类似：

```text
V2.14.2.3_操作员包_<commit>_QUICKDEPLOY_R10.7z
V2.14.2.3_操作员包_<commit>_QUICKDEPLOY_R10.7z.sha256
```

脚本会自动：

- 复制完整受控 Release 内容；
- 添加“一键安装正式版”“一键修复”“一键卸载”和“检查运行状态”；
- 生成快捷部署包身份文件；
- 生成包内逐文件 SHA-256；
- 使用 7-Zip 压缩；
- 对生成的 `.7z` 执行完整性测试；
- 生成外层 `.sha256` 文件。

`.7z` 和对应的 `.sha256` 必须作为一组交付。

## 8. 开发电脑交付前检查

### 8.1 验证压缩包哈希

```powershell
$archive = '<完整的 .7z 路径>'
$expected = ((Get-Content -LiteralPath ($archive + '.sha256') -Raw).Trim() `
    -split '\s+')[0].ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()

if ($actual -ne $expected) {
    throw "压缩包 SHA-256 不一致：Expected=$expected Actual=$actual"
}

"PASS SHA256=$actual"
```

### 8.2 验证压缩文件

```powershell
& 'C:\Program Files\7-Zip\7z.exe' t $archive
```

应看到 7-Zip 测试成功，不得出现 CRC 或数据错误。

### 8.3 保存发布记录

至少记录：

- 产品版本；
- Git commit；
- Release 目录；
- `.7z` 文件名；
- `.7z` SHA-256；
- 构建时间；
- 执行人员；
- 自动化测试最终摘要。

## 9. 将包传到远程实际机

复制以下两个文件：

```text
<操作员包>.7z
<操作员包>.7z.sha256
```

建议放入实际机本地目录，例如：

```text
D:\MTTFTestDeploy\V2.14.2.3\
```

要求：

- 不要在邮件附件、压缩包预览或网络共享中直接运行；
- 传输完成后在实际机重新计算 SHA-256；
- 完整解压到一个新的本地目录；
- 不要覆盖旧的解压目录；
- 保留上一版操作员包，便于审计和故障分析。

## 10. 实际机更新前准备

### 10.1 使用生产账号登录

使用以后长期运行试验的 Windows 账号登录。桌面快捷方式和登录任务将为该生产账号创建。

### 10.2 安全停止旧程序

如果旧程序正在试验：

1. 点击“停止试验”；
2. 等待 DO OFF、AO 归零、电源处置和数据落盘完成；
3. 确认监控窗和主程序完全退出；
4. 不要在活动试验或写盘过程中直接结束进程。

安装脚本检测到已安装的 `MTTFTest.exe` 仍在运行时，会拒绝更新。

### 10.3 建议备份运行配置

升级程序不会覆盖已有 ProgramData 配置，只补充缺失文件。现场更新前仍建议复制备份：

```text
C:\ProgramData\MTTFTest\Config
```

不要把备份放回程序 `Current\Config`，运行配置的权威目录是 ProgramData。

## 11. 在实际机执行更新

### 11.1 首选方式

进入完整解压后的新目录，双击第一层：

```text
MTTFTest.exe
```

新版本高于健康的已安装版本时：

1. Windows 弹出一次管理员权限提示；
2. 选择“是”；
3. 程序停止旧后台组件；
4. 迁移或补齐 ProgramData 配置；
5. 替换安装目录中的 `Current`；
6. 配置 Supervisor 服务；
7. 配置 SessionAgent 和主程序登录任务；
8. 重建桌面及开始菜单快捷方式；
9. 启动安装后的正式程序。

以后使用桌面快捷方式启动，不再需要管理员权限，也不会重复部署。

### 11.2 明确手工安装

需要维护人员明确重新执行安装时，可双击：

```text
一键安装正式版.cmd
```

### 11.3 修复现有安装

以下情况使用：

```text
一键修复.cmd
```

- ProgramData 配置目录缺失或权限错误；
- Supervisor 服务缺失；
- SessionAgent 或登录任务缺失；
- 桌面快捷方式损坏；
- 首次配置中断。

维修会要求一次管理员权限，维修完成后应用程序仍以普通权限运行。

## 12. 更新后检查

### 12.1 检查服务、任务和进程

双击解压包中的：

```text
检查运行状态.cmd
```

重点确认：

- `MTTFTestSupervisor` 服务为 `Running`；
- `MTTFTestSessionAgent` 任务存在；
- `MTTFTestAutoStart` 任务存在；
- 主程序进程路径指向安装目录的 `Current\MTTFTest.exe`；
- 没有同时运行两个不同目录的主程序。

### 12.2 检查运行配置

确认目录存在且包含 8 个 XML：

```text
C:\ProgramData\MTTFTest\Config
```

确认普通生产账号可以在该目录创建并删除临时文件。程序不应再尝试写入：

```text
<安装目录>\Current\Config\*.tmp
```

### 12.3 检查普通权限启动

1. 完全退出程序；
2. 双击桌面快捷方式；
3. 不应弹出 UAC；
4. 不应出现 `TestConfig.xml.tmp` 访问被拒绝；
5. 不应再次显示服务部署过程；
6. 同版本启动不应新增 `.retired-*` 目录。

### 12.4 检查空闲监控关闭

使用虚拟 NI `Dev1`、`Dev2` 且不连接程控电源时：

1. 打开监控界面；
2. 不点击“开始试验”；
3. 直接点击监控窗 `×`；
4. 正常目标为 3 秒内关闭；
5. 日志中不应出现四台程控电源的连接、查询或关闭请求。

### 12.5 检查活动试验

有真实硬件时还应验证：

- 没有通过电源预检时不能开始试验；
- 活动试验关闭仍按顺序执行 DO OFF、AO 归零、电源 OFF、DAQ 停止和数据落盘；
- 电源断联时记录 `CommunicationUnavailableSkipped`；
- 电源断联不得记录 `PowerOffConfirmed=true`；
- 明确回读 `OUTP ON` 时仍进入 Supervisor/SafetyAgent 安全接管。

## 13. 常见错误处理

### 13.1 “源码树不是干净状态”

原因：存在未提交或未跟踪文件。

处理：

```powershell
git status --short
git diff
```

确认并提交应该发布的文件，移除不属于源码的本地产物。不要使用 `-AllowDirtyCandidate` 制作实际机包。

### 13.2 “MSBuild 不存在”

使用 `vswhere.exe` 查询真实路径，并通过 `-MsBuild` 传入。若查询不到，安装 Visual Studio 的 MSBuild 和 .NET Framework 4.8 开发组件。

### 13.3 编译提示 NI、CAN 或供应商程序集缺失

在开发电脑补齐对应 SDK 和引用环境。不要从旧实际机随意复制单个 DLL 拼包。

### 13.4 自动化测试失败

失败后不会产生可认可的正式包。保存完整控制台输出，修复失败原因后从干净提交重新执行，不要使用失败构建的 `bin\Release`。

### 13.5 找不到 7-Zip

安装 7-Zip，确认以下路径之一存在：

```text
C:\Program Files\7-Zip\7z.exe
C:\Program Files (x86)\7-Zip\7z.exe
```

### 13.6 实际机提示旧主程序仍在运行

回到旧程序执行安全停止并完全退出。不要通过任务管理器强杀仍在活动试验或落盘中的主程序。

### 13.7 双击新包却直接打开旧 Current，没有升级

优先检查：

- 新包版本是否高于已安装版本；
- 是否误用了旧 Release 目录；
- 快捷部署包中的 `build-identity.json` 是否对应本次 commit；
- 新包是否完整解压。

同版本或更低版本不会替换健康的 `Current`，这是防止重复部署和重复创建 `.retired-*` 的设计。

### 13.8 ProgramData 配置访问失败

以管理员身份执行新包中的：

```text
一键修复.cmd
```

修复后退出管理员安装窗口，再通过桌面快捷方式以普通权限启动。

### 13.9 更新失败后的处理

- 保留安装窗口的完整错误信息；
- 保留新旧操作员包及 `.sha256`；
- 保留 `C:\ProgramData\MTTFTest` 下的日志和事故证据；
- 首先尝试同版本新包的“一键修复”；
- 不要手工移动或删除 `Current`、`.retired-*`、`LastKnownGood`；
- 旧版本包默认不会覆盖更高版本，降级或槽回滚应按专门维护流程执行。

## 14. 发布完成检查清单

### 开发电脑

- [ ] 版本号已经按发布计划更新且各组件一致；
- [ ] Git 修改已审查并提交；
- [ ] `git status --short` 无输出；
- [ ] `New-FormalRelease7z.ps1` 显示“交付成功”；
- [ ] 最终输出显示 `Dirty=False`；
- [ ] 已记录明确的 `PackageOutput`；
- [ ] `Verify-Release.ps1` 通过；
- [ ] `.7z` 完整性测试通过；
- [ ] `.7z` SHA-256 与 `.sha256` 一致；
- [ ] `.7z` 和 `.sha256` 成对交付。

### 远程实际机

- [ ] 使用生产账号登录；
- [ ] 旧试验已安全停止并完成落盘；
- [ ] 旧主程序已完全退出；
- [ ] 新包已复制到本地磁盘；
- [ ] 实际机重新计算的 SHA-256 一致；
- [ ] 新包已完整解压到独立目录；
- [ ] 双击新包第一层 `MTTFTest.exe` 并接受一次 UAC；
- [ ] 服务和计划任务状态正常；
- [ ] 进程从安装目录 `Current` 启动；
- [ ] ProgramData 配置完整且普通账号可写；
- [ ] 桌面快捷方式普通权限启动无 UAC；
- [ ] 空闲监控窗关闭验证通过；
- [ ] 真机安全停机和数据落盘验证已按发布级别完成。

## 15. 最简标准命令汇总

在干净 Git 工作区执行：

```powershell
Set-Location 'D:\Github\wanxiang\EPBTest'

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -products * `
    -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\MSBuild.exe'

.\Tools\New-FormalRelease7z.ps1 -MsBuild $msbuild
```

随后把 `artifacts\deploy` 中生成的 `.7z` 和 `.7z.sha256` 复制到实际机，重新核对哈希，完整解压并双击第一层 `MTTFTest.exe`。
