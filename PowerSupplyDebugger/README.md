# GW Instek PSW 四机调试工具

独立的 `.NET 8 / WPF / MVVM` 调试程序，用于连接、读取和逐台控制四台 GW Instek PSW 程控电源。程序不复用 `MTTfTest` 中的历史电源控制代码。

## 默认设备

| 设备 | 地址 |
|---|---|
| 电源 1 | `192.168.1.101:2268` |
| 电源 2 | `192.168.1.102:2268` |
| 电源 3 | `192.168.1.103:2268` |
| 电源 4 | `192.168.1.104:2268` |

`2268` 是 GW Instek PSW 原生 Socket Server 端口。历史资料中的 `30000` 属于旧记录或代理/转发候选端口，不作为当前直连配置。

## 安全行为

- 连接只执行查询，不写入 VSET、ISET、OVP、OCP 或 OUTP。
- 连接前设备若为 `OUTP ON`，连接后仍保持 ON，并显示红色告警。
- 只有 `*IDN?` 识别为 GW Instek PSW 30-72/30-108 后，写入按钮才可用。
- 参数和输出仅能逐台写入；不提供批量设定或批量 OUTP。
- `OUTP ON` 必须人工确认；`OUTP OFF` 可直接执行。
- 输出为 ON 时修改参数，会显示原值、新值与允许范围并再次确认。
- 所有设置与 OUTP 操作都进行立即回读；命令失败时不自动重试。
- 自动轮询每 2 秒读取核心状态，但不读取错误队列。

## 构建、测试与发布

```powershell
dotnet build .\PowerSupplyDebugger\PowerSupplyDebugger.csproj -c Release
dotnet test .\Tests\PowerSupplyDebugger.Tests\PowerSupplyDebugger.Tests.csproj -c Release
dotnet publish .\PowerSupplyDebugger\PowerSupplyDebugger.csproj -c Release -r win-x64 --self-contained true -o .\开发相关\04_程控电源\PowerSupplyDebugger-win-x64
```

发布目录中的 `PowerSupplyDebugger.exe` 可在未安装 .NET Runtime 的 64 位 Windows 现场电脑运行。

## 本地数据

- 配置：`%LocalAppData%\EPBTest\PowerSupplyDebugger\devices.json`
- 日志：`%LocalAppData%\EPBTest\PowerSupplyDebugger\Logs\yyyy-MM-dd.log`

修改 IP、端口或终止符前，应先断开设备。默认 SCPI 终止符为 `CRLF`（配置文本写作 `\r\n`）。

## 现场验收

首次连接四台真实设备时，应逐台保存 `*IDN?` 原始响应，并将型号、序列号、固件版本和现场资产编号绑定。应先验证各设备 `IP:2268` 的 TCP 可达性，再确认控制来源查询兼容性及保护参数范围。
