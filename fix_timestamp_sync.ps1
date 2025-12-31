# 修复两个数采卡时间戳不同步的问题
$file Path = "d:\Github\wanxiang\EPBTest\IO.NI\TwoDeviceAiAcquirer.cs"
$lines = [System.Collections.ArrayList](Get-Content $filePath)

# 找到要替换的起始和结束行
$startIdx = -1
$endIdx = -1

for ($i = 0; $i < $lines.Count; $i++) {
    if ($lines[$i] -match "② 取本设备的时钟状态") {
        $startIdx = $i
    }
    if ($startIdx -ge 0 -and $lines[$i] -match "var current = Math\.Abs\(driftMs\) > 5 \? hostNow : idealNow;") {
        $endIdx = $i
        break
    }
}

if ($startIdx -ge 0 -and $endIdx -ge 0) {
    Write-Host "找到目标范围: 行 $($startIdx+1) 到 $($endIdx+1)"
    
    # 准备新内容
    $newLines = @(
        "                // ② 使用全局统一的时间基准（两设备共用，避免漂移）",
        "                DateTime last, current;",
        "                double driftMs;",
        "                lock (_globalLastLock)",
        "                {",
        "                    last = _globalLast;",
        "                    ",
        "                    // 两种时间：主机`"实测`" + 按采样率推进的`"理想`"",
        "                    var hostNow = _t0.AddMilliseconds(_sw.ElapsedMilliseconds - _ts0); // 主机`"实测时间`"",
        "                    var idealNow = last.AddSeconds(n / _sampleRate); // 理想时间：由采样率推进，避免 jitter 抖动",
        "",
        "                    // ③ 轻微纠偏（例如 >5ms 时用主机时间，否则用理想时间，避免长期漂移）",
        "                    driftMs = (hostNow - idealNow).TotalMilliseconds;",
        "                    current = Math.Abs(driftMs) > 5 ? hostNow : idealNow;",
        "                    ",
        "                    // ④ 原子更新全局时间基准（两设备共享，确保同步）",
        "                    _globalLast = current;",
        "                }"
    )
    
    # 删除旧行并插入新行
    for ($i = $endIdx; $i >= $startIdx; $i--) {
        $lines.RemoveAt($i)
    }
    
    $lines.InsertRange($startIdx, $newLines)
    
    # 写回文件
    $lines | Set-Content $filePath -Encoding UTF8
    Write-Host "替换成功！"
} else {
    Write-Host "未找到目标代码段"
}
