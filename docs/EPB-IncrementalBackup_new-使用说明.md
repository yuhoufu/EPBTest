# EPB 新版增量备份脚本完整使用说明

本文档适用于：

- 脚本：`Tools\EPB-IncrementalBackup_new.ps1`
- 运行环境：Windows PowerShell 5.1
- 备份对象：一个 EPB 项目数据目录
- 备份方式：一个完整基包，加若干按顺序衔接的增量包
- Python：不需要安装

## 1. 功能概览

新版脚本提供以下能力：

- 第一次备份自动创建完整基包。
- 后续只打包新增、修改或删除的数据。
- 自动读取 `MTTFTest.exe` 的程序版本。
- 优先读取项目 `Config\runtime-build-identity.json`，并与正在运行或明确指定的 EXE 交叉校验。
- 对正在使用的 `index.db` 创建一致的 SQLite 快照。
- 识别 WAL 模式下尚未合并进主数据库的数据。
- 对快照文件计算 SHA-256，并在压缩前回读验证。
- 压缩后使用 7-Zip 测试归档完整性。
- 在每个压缩包中写入 `backup-manifest.json`，明确记录基包、增量顺序和删除文件。
- 只有归档创建并验证成功后，才更新本地备份状态。

脚本不会上传压缩包，也不会自动删除旧压缩包。

## 2. 现场部署文件

建议将以下文件放在同一个目录：

```text
EPB-Backup\
├─ EPB-IncrementalBackup_new.ps1
├─ System.Data.SQLite.dll
├─ x86\
│  └─ SQLite.Interop.dll
└─ x64\
   └─ SQLite.Interop.dll
```

同时需要安装 7-Zip，默认位置为：

```text
C:\Program Files\7-Zip\7z.exe
```

注意：

- `System.Data.SQLite.dll` 必须和项目使用的版本兼容。
- `x86`、`x64` 目录名及目录层级不能改变。
- 使用 32 位 PowerShell 时加载 `x86\SQLite.Interop.dll`。
- 使用 64 位 PowerShell 时加载 `x64\SQLite.Interop.dll`。
- 如果脚本与 `MTTFTest.exe` 放在同一目录，可直接复用程序目录中的 SQLite 组件。
- 现场机不需要 Python。

## 3. 参数说明

| 参数 | 必填 | 默认值 | 说明 |
| --- | --- | --- | --- |
| `-SourceDir` | 建议指定 | `D:\EPB_Data\10358-029a` | 要备份的项目数据目录。目录名会作为项目名称。 |
| `-OutputDir` | 建议指定 | `D:\EPB_Data` | `.7z` 备份包输出目录。 |
| `-WorkRoot` | 建议指定 | `D:\EPB_BackupWork` | 状态文件和临时快照目录。不能放在 `SourceDir` 内部。 |
| `-SevenZip` | 否 | `C:\Program Files\7-Zip\7z.exe` | `7z.exe` 的完整路径。 |
| `-ApplicationExecutable` | 建议指定 | 空 | 当前版本 `MTTFTest.exe` 的完整路径。用于自动读取版本。 |
| `-Version` | 否 | 空 | 手工指定版本，例如 `2.13.0.28` 或 `V2.13.0.28`。最终统一为 `V2.13.0.28`。 |
| `-SQLiteAssemblyPath` | 否 | 空 | `System.Data.SQLite.dll` 的完整路径。自动查找失败时使用。 |
| `-StartNewBase` | 否 | 未启用 | 主动结束当前链，并创建一个新的完整基包。 |

## 4. 最推荐的运行方式

在脚本目录打开 PowerShell，执行：

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass `
  -File ".\EPB-IncrementalBackup_new.ps1" `
  -ApplicationExecutable "D:\EPB\MTTFTest.exe" `
  -SourceDir "D:\EPB_Data\10358-029a" `
  -OutputDir "D:\EPB_Backups" `
  -WorkRoot "D:\EPB_BackupWork"
```

第一次执行会生成完整基包。以后使用完全相同的命令执行，脚本会自动生成增量包；如果数据没有变化，则不生成压缩包并正常退出。

## 5. 常见运行示例

### 5.1 第一次备份

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "D:\EPB_Data\EPB-IncrementalBackup_new.ps1" `
  -ApplicationExecutable "D:\EPB\MTTFTest.exe" `
  -SourceDir "D:\EPB_Data\10358-029a" `
  -OutputDir "D:\EPB_Data\Backups" `
  -WorkRoot "D:\EPB_BackupWork"
```

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File ".\EPB-IncrementalBackup_new.ps1" `
  -SourceDir "D:\EPB_Data\10358-029a" `
  -OutputDir "D:\EPB_Data\Backups" `
  -WorkRoot "D:\EPB_BackupWork"
```



如果没有这个项目的备份链状态，脚本会创建 `B0001` 完整基包。

### 5.2 日常增量备份

继续执行与第一次完全相同的命令即可，不要添加 `-StartNewBase`。

可能生成：

```text
10358-029a_V2.13.0.28_I0002_0828_1800.7z
```

### 5.3 强制开始一条新备份链

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "D:\EPB_Data\EPB-IncrementalBackup_new.ps1" `
  -ApplicationExecutable "D:\EPB\MTTFTest.exe" `
  -SourceDir "D:\EPB_Data\10358-029a" `
  -OutputDir "D:\EPB_Data\Backups" `
  -WorkRoot "D:\EPB_BackupWork" `
  -StartNewBase
```

适合以下场景：

- 需要建立新的长期保留点。
- 旧备份包不完整或无法确认链条。
- 计划清理、离线转移旧备份链。
- 重大升级后希望恢复时不依赖旧版本时期的基包。

`-StartNewBase` 会产生完整数据包，数据量可能很大。创建成功后，后续备份归入新链。

### 5.4 手工指定版本号

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "D:\EPB_Data\EPB-IncrementalBackup_new.ps1" `
  -Version "V2.13.0.28" `
  -SourceDir "D:\EPB_Data\10358-029a" `
  -OutputDir "D:\EPB_Data\Backups" `
  -WorkRoot "D:\EPB_BackupWork"
```

如果脚本同时找到了 `MTTFTest.exe`，手工版本必须与 EXE 版本一致，否则脚本会终止，以避免备份包被标错版本。

### 5.5 明确指定 SQLite 组件

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File "D:\EPB_Data\EPB-IncrementalBackup_new.ps1" `
  -ApplicationExecutable "D:\EPB\MTTFTest.exe" `
  -SQLiteAssemblyPath "D:\EPB\System.Data.SQLite.dll" `
  -SourceDir "D:\EPB_Data\10358-029a" `
  -OutputDir "D:\EPB_Data\Backups" `
  -WorkRoot "D:\EPB_BackupWork"
```

## 6. 程序版本号如何确定

主程序在启动、项目打开、创建、切换和保存成功后，会原子更新：

```text
项目目录\Config\runtime-build-identity.json
```

该文件记录项目名称、四段程序版本、EXE 路径和 SHA-256、进程信息、构建信息、发布包校验结果及捕获时间。临时文件与目标文件位于同一目录，写入内容完成并刷新到磁盘后才替换正式文件，避免断电留下半个 JSON。

省略 `-Version` 时，备份脚本优先读取这个项目身份文件，并将版本规范化为带 `V` 的四段版本号。

校验顺序如下：

1. 校验身份文件的 `schemaVersion`、`projectName`、`productVersion` 和 `capturedUtc`。
2. 如果传入 `-ApplicationExecutable`，与该 EXE 版本交叉核对。
3. 否则如果存在正在运行的 `MTTFTest` 进程，与运行进程 EXE 交叉核对。
4. 否则尝试身份文件记录的 EXE 路径。
5. 如果同一路径的 EXE 仍存在，同时核对其 SHA-256。
6. EXE 当前不可访问时，允许使用项目身份文件中最后一次成功激活项目时记录的版本。

任何项目名、版本或同路径 EXE 哈希不一致都会终止备份，避免生成身份标错的备份包。

旧项目还没有 `runtime-build-identity.json` 时，脚本兼容按以下范围寻找 EXE：

1. `-ApplicationExecutable` 明确指定的位置。
2. 脚本所在目录。
3. 当前 PowerShell 工作目录。
4. `SourceDir` 项目数据目录。
5. 当前正在运行的 `MTTFTest` 进程目录。

新版主程序已经在项目 Config 中保存可信的最近运行身份。现场仍推荐传入 `-ApplicationExecutable`，从而在备份时完成身份文件与实际程序的双重校验。

如果发现多个不同位置的 `MTTFTest.exe`，应使用 `-ApplicationExecutable` 消除歧义。

软件版本变化不会自动创建新基包。例如从 `V2.13.0.28` 升级到 `V2.13.0.29` 后，默认仍然接续当前链，只是新压缩包会记录新版本。如果升级后需要独立恢复链，应在升级后的第一次备份中添加 `-StartNewBase`。

## 7. 如何区分基包与增量包

压缩包名称格式为：

```text
项目名_程序版本_链节点_月日_时分.7z
```

示例：

```text
10358-029a_V2.13.0.28_B0001_0828_1745.7z
10358-029a_V2.13.0.28_I0002_0828_1800.7z
10358-029a_V2.13.0.29_I0003_0829_0900.7z
```

- `B0001`：完整基包，是当前链的起点。
- `I0002`：当前链第 2 个节点，是增量包。
- `I0003`：当前链第 3 个节点，是增量包。
- `B` 表示 Base，`I` 表示 Incremental。
- 数字是链节点序号，不是软件版本号。

如果同一分钟内生成重名文件，脚本会自动增加 `_01`、`_02` 等后缀。

文件名便于人工识别，但恢复链的最终依据应是压缩包内的 `backup-manifest.json`，不能只依赖文件名。

## 8. 什么时候会创建基包

满足任一条件时创建完整基包：

- 项目第一次运行新版脚本。
- 本地有旧版 CSV 状态，但没有新版备份链状态；脚本会通过完整基包迁移到可验证链。
- 命令中添加了 `-StartNewBase`。

其他情况下生成增量包。程序版本号改变本身不会自动创建新基包。

## 9. 状态文件说明

默认状态目录：

```text
D:\EPB_BackupWork\State\
```

每个项目有两个主要状态文件：

```text
10358-029a.csv
10358-029a.backup-chain.json
```

### 9.1 文件状态 CSV

CSV 记录已经成功备份的文件信息，用来判断下一轮有哪些文件新增、修改或删除。

### 9.2 备份链 JSON

备份链 JSON 记录：

- 当前基包 ID 和文件名。
- 最后一个成功包的 ID、序号和文件名。
- 最后一次备份时的软件版本。

只有压缩、归档回读和状态写入均成功后，脚本才更新这两个状态文件。

不要把同一个 `WorkRoot` 状态目录随意复制到另一台机器后继续写入，除非对应的全部备份包也已完整复制。不要手工编辑状态文件。

## 10. 压缩包内的清单

每个压缩包根目录包含：

```text
backup-manifest.json
```

关键字段包括：

| 字段 | 含义 |
| --- | --- |
| `project` | 项目名称。 |
| `application_version` | 本次备份使用的软件版本。 |
| `application_version_source` | 版本号来源。 |
| `project_runtime_identity_file` | 项目身份文件相对路径；新版为 `Config/runtime-build-identity.json`。 |
| `project_runtime_identity_captured_utc` | 主程序最近一次发布项目身份的 UTC 时间。 |
| `backup_kind` | `base` 或 `incremental`。 |
| `backup_id` | 当前包唯一 ID。 |
| `backup_sequence` | 当前节点序号。 |
| `base_backup_id` | 当前链基包 ID。 |
| `previous_backup_id` | 前一个节点 ID；基包为空。 |
| `archive_file_name` | 预期压缩包文件名。 |
| `captured_utc` | 快照时间。 |
| `snapshot_verified` | 快照是否已验证。 |
| `contains_deletions` | 本轮是否包含删除操作。 |
| `deleted_paths` | 本轮应从恢复目录删除的相对路径。 |
| `files` | 本轮文件、长度、SHA-256 和 SQLite 完整性信息。 |

判断一组包是否为连续链时，应确认：

1. 第一个包的 `backup_kind` 是 `base`。
2. 所有包的 `base_backup_id` 与基包的 `backup_id` 相同。
3. 每个增量包的 `previous_backup_id` 等于前一个包的 `backup_id`。
4. `backup_sequence` 从 1 开始连续递增。
5. 项目名称一致。

## 11. SQLite 和运行中数据的处理

当项目中存在 `index.db` 时，脚本使用 `System.Data.SQLite` 创建一致快照，而不是直接复制一个可能正在写入的数据库文件。

脚本会：

- 识别主数据库以及 WAL 变化。
- 通过 SQLite 备份接口生成快照。
- 对快照执行 `PRAGMA integrity_check`。
- 在清单中记录完整性结果。
- 避免把活动中的 SQLite sidecar 文件作为普通文件直接归档。

因此，在程序运行期间也可以执行备份。但现场首次启用时，仍建议安排一次人工恢复演练。

## 12. 文件删除如何记录

如果某个已备份文件后来从项目目录中删除，增量包的 `deleted_paths` 会记录该相对路径。

如果本轮只有文件删除，没有新增或修改，脚本仍会创建一个只包含清单的增量包。这样恢复结果才能与原目录精确一致。

如果完全没有新增、修改或删除，脚本不生成 `.7z`，退出码为 `0`。

### 12.1 扫描后文件被删除或轮转

主程序可能在备份扫描结束后，因历史数据保留或文件轮转删除某个 `HistoricalSnapshots` 文件。新版脚本会再次确认该源文件是否真的已不存在：

- 已不存在：显示警告，但继续完成备份。
- 增量包：把该路径自动加入 `deleted_paths`，恢复时会删除对应旧文件。
- 基包：该文件本来已不在最终源目录，因此不会写入基包；清单的 `source_files_disappeared_during_snapshot` 会保留审计记录。
- 文件仍存在但无法读取：例如拒绝访问、设备 I/O 错误、共享冲突持续失败，仍会终止备份，避免静默遗漏仍存在的数据。

这意味着“找不到路径”的并发删除不会再导致整轮备份失败；但权限或读取错误仍必须人工处理。

## 13. 恢复流程

当前脚本负责备份，不负责自动恢复。恢复时建议在空目录操作，不要直接覆盖现场唯一数据。

### 13.1 确认完整备份链

例如：

```text
B0001 → I0002 → I0003 → I0004
```

先检查每个包内的 `backup-manifest.json`，确认 ID 和序号连续。

### 13.2 按顺序解压

1. 把 `B0001` 解压到空的恢复目录。
2. 把 `I0002` 解压并覆盖到同一目录。
3. 按相同方式依次应用 `I0003`、`I0004`。
4. 每应用一个增量包，都要读取该包的 `deleted_paths`，删除恢复目录中的对应相对路径。
5. `backup-manifest.json` 是控制清单，不是项目业务数据；完成核验后可从最终业务目录移除。

不要跳过中间增量包。仅解压最后一个增量包不能得到完整项目。

### 13.3 恢复后验证

至少检查：

- 项目目录结构是否完整。
- 关键 CSV 文件能否读取。
- `index.db` 能否正常打开。
- SQLite `PRAGMA integrity_check` 是否返回 `ok`。
- MTTFTest 是否能识别并打开恢复项目。
- 恢复版本与计划使用的程序版本是否匹配。

## 14. 备份包转移与保留建议

一条链中的基包和全部后续增量包应作为一个整体保存。例如保留 `I0005` 时，必须同时保留：

```text
B0001、I0002、I0003、I0004、I0005
```

建议：

- 完成基包后立即复制到独立磁盘或服务器。
- 增量包生成后同步复制。
- 定期使用 `-StartNewBase` 建立新链，降低长期链条丢包风险。
- 新链完成并完成恢复验证前，不要删除旧链。
- 每次转移后用 `7z t` 测试归档。

示例：

```powershell
& "C:\Program Files\7-Zip\7z.exe" t "D:\EPB_Data\Backups\10358-029a_V2.13.0.28_B0001_0828_1745.7z"
```

## 15. 计划任务建议

可以通过 Windows 任务计划程序定时调用一个固定命令。建议设置：

- 使用具有源目录、程序目录、输出目录读写权限的账户。
- 使用最高权限运行。
- 操作为 `powershell.exe`。
- 参数中使用脚本和所有目录的绝对路径。
- 不要把 `-StartNewBase` 放进日常计划任务。
- 将任务失败记录纳入现场巡检。

计划任务参数示例：

```text
-NoLogo -NoProfile -ExecutionPolicy Bypass -File "D:\EPB_Data\EPB-IncrementalBackup_new.ps1" -ApplicationExecutable "D:\EPB\MTTFTest.exe" -SourceDir "D:\EPB_Data\10358-029a" -OutputDir "D:\EPB_Data\Backups" -WorkRoot "D:\EPB_BackupWork"
```

## 16. 常见故障排查

### 16.1 提示“未找到 7-Zip”

确认 7-Zip 已安装，或通过参数指定：

```powershell
-SevenZip "D:\Tools\7-Zip\7z.exe"
```

### 16.2 提示“未找到 System.Data.SQLite.dll”

采用任一方法：

- 把脚本放到 `MTTFTest.exe` 同一目录。
- 把 `System.Data.SQLite.dll`、`x86\SQLite.Interop.dll`、`x64\SQLite.Interop.dll` 随脚本部署。
- 使用 `-SQLiteAssemblyPath` 指定 DLL。

### 16.3 提示“加载 System.Data.SQLite 失败”

重点检查：

- DLL 是否来自同一套发布文件。
- `x86`、`x64` 子目录是否存在。
- PowerShell 位数是否与 native DLL 匹配。
- DLL 是否被 Windows 标记为来自互联网并被阻止。
- Microsoft Visual C++ 运行库是否满足该 SQLite 版本要求。

### 16.4 提示手工版本与 EXE 版本不一致

不要强行修改压缩包名称。确认实际使用哪个 `MTTFTest.exe`，然后修正 `-ApplicationExecutable` 或 `-Version`。

### 16.5 提示项目运行身份不一致

先停止备份并核对：

- `SourceDir` 是否指向当前项目。
- 当前运行的 `MTTFTest.exe` 是否为正确发布版本。
- 项目是否从另一台机器复制后尚未被当前主程序打开。
- EXE 是否在身份文件生成后被替换或打补丁。

不要手工修改 `runtime-build-identity.json`。使用正确的主程序重新打开或切换到该项目，让主程序原子刷新身份文件，再重新备份。

### 16.6 提示备份链状态无效

先保留现场状态文件和已有压缩包用于调查。如果已有链无法确认，可使用 `-StartNewBase` 创建一条新的完整链。

### 16.7 7-Zip 压缩失败

脚本不会更新成功状态，下次运行仍会重新识别这些数据。临时快照目录会保留，便于排查磁盘空间、权限或文件系统问题。

### 16.8 没有生成压缩包

如果输出提示没有新增、修改或删除，这是正常结果，退出码为 `0`。

### 16.9 提示文件在扫描后已删除或轮转

这是警告，不是失败。表示文件在扫描后被运行程序清理，备份已按最终目录状态继续完成。请保留压缩包内的 `backup-manifest.json`，其中会记录 `source_files_disappeared_during_snapshot`；增量包还会在 `deleted_paths` 中记录恢复时需要删除的旧文件。

## 17. 上线前检查清单

- [ ] 使用的是 `EPB-IncrementalBackup_new.ps1`。
- [ ] 脚本保持 UTF-8 BOM 编码。
- [ ] Windows PowerShell 5.1 能正常解析脚本。
- [ ] 7-Zip 路径正确。
- [ ] SQLite 托管 DLL 和 x86/x64 native DLL 齐全。
- [ ] `SourceDir` 指向正确项目。
- [ ] `OutputDir` 有足够空间。
- [ ] `WorkRoot` 不在 `SourceDir` 内部。
- [ ] 明确指定了正确的 `MTTFTest.exe`。
- [ ] 项目 `Config\runtime-build-identity.json` 存在且项目名、版本正确。
- [ ] 第一次运行成功生成 `B0001`。
- [ ] 第二次有变化时成功生成 `I0002`。
- [ ] 无变化时不会生成空备份包。
- [ ] 已把基包和增量包复制到独立存储。
- [ ] 已完成至少一次完整恢复演练。

## 18. 现场操作原则

1. 日常备份使用固定命令，不带 `-StartNewBase`。
2. 基包和全部增量包必须成链保存。
3. 不手工修改 CSV、备份链 JSON 或包内清单。
4. 不要仅凭文件名判断链条，最终以 `backup-manifest.json` 的 ID 关系为准。
5. 新链验证成功前不要删除旧链。
6. 任何备份都不能替代定期恢复演练。
