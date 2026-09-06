# ThresholdExporter（阈值记录一键导出）

用途：从运行日志 `ErrorLog-*.txt` 中提取所有“阈值记录”行，导出为与
`运行日志/CB32ms_异常清单_ErrorLog-1228-1.csv` **同列名/同顺序**的 CSV。

## 输出 CSV 列
默认输出列（与模板一致）：

- `LineNo`
- `DateTime`
- `Hour`
- `Channel`
- `ThresholdA`
- `CutoffA`
- `CbMs`
- `ArrivalDelayMs`
- `Dev`
- `N`
- `FsHz`
- `ImaxA`

> 建议使用 `--template` 指定模板 CSV，这样即使未来模板列顺序变更，导出也能保持一致。

## 运行环境
- Windows
- Python 3.8+（仅用标准库，无第三方依赖）

> 说明：如果你的 `python` 命令解析到 `C:\Users\...\WindowsApps\python.exe`（微软商店占位符），可能会出现“无输出/卡住”。
> 建议使用 Windows 自带的 Python Launcher：`py -3`。

## 用法
在仓库根目录（`EPBTest/`）打开 PowerShell：

### 1）导出单个日志文件
```powershell
py -3 .\Tools\ThresholdExporter\export_thresholds.py `
  --log .\运行日志\ErrorLog-1228-1.txt `
  --template .\运行日志\CB32ms_异常清单_ErrorLog-1228-1.csv `
  --out .\运行日志\ALL_阈值记录_ErrorLog-1228-1.csv
```

### 2）导出整个目录（递归收集所有 *.txt）
```powershell
py -3 .\Tools\ThresholdExporter\export_thresholds.py `
  --log .\运行日志 `
  --template .\运行日志\CB32ms_异常清单_ErrorLog-1228-1.csv `
  --out .\运行日志\ALL_阈值记录_运行日志目录.csv
```

脚本会输出扫描行数与命中条数。

## 解析规则说明（匹配哪些行）
脚本只匹配包含以下关键信息的“阈值记录”日志行：
- `EPB[通道]，阈值：xxA`
- `断电触发点：xxA`
- `触发时回调间隔≈xxms 到达延迟≈xxms (Dev? N=? Fs=?Hz)`
- `Imax=xxA @ hh:mm:ss.fff`

不包含这些字段的状态/提示行会被跳过。

## 常见问题
- 输出 CSV 里某些 `Dev/N/FsHz` 为 0：说明该行日志里没有匹配到 `(Dev? N=? Fs=?Hz)` 字段（或格式不同）。
- 乱码：脚本输出为 UTF-8 with BOM（`utf-8-sig`），Excel 直接打开一般不会乱码。
