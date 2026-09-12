## 连接现场机

远程机器：
主机名：`wj-epb`

账户名：`mt`

直接通过下述指令远程：

> 连接现场主机，本机终端执行

```powershell
 Enter-PSSession -Session (Get-WjEpbSession)
```

## 临时运行运行PS1

程序安装包存放目录：`D:\debug`

> 临时放宽当前 PowerShell 会话的脚本执行限制，允许在这个会话中运行 `.ps1` 脚本。关闭会话后失效

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
```

## 安装包存放

程序安装包存放目录：`D:\debug`

> 正在运行2.14.2.11的安装包路径

```powershell
D:\debug\MTTFTest_V2.14.2.11_20260902_222838
```

### 封存项目数据

项目数据存储目录：`D:\EPB_Data`

以下指令皆在`wj-epb` 中的 `D:\EPB_Data`中运行

> 数据封装指令：

```powershell
& ".\EPB-IncrementalBackup_new.ps1" `
-SourceDir "D:\EPB_Data\10243-028" `
-OutputDir "D:\EPB_Data\Backups" `
-WorkRoot "D:\EPB_BackupWork"
```

> 清除日志指令：

```powershell
Remove-Item -Recurse -Force ".\10243-028\log"
```

## 封存数据取回本机

本机数据存储目录：`D:\EPB_Data`

以下指令皆在本机中运行

> 封存数据取回：

```powershell
robocopy "\\wj-epb\EPB_Data\Backups" "D:\EPB_Data\Backups" "*.7z" /COPY:DAT /DCOPY:DAT /J /Z /R:3 /W:5 /TEE
```
