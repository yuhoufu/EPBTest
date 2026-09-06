# -*- coding: utf-8 -*-
"""
修复两个数采卡时间同步问题
"""
import re

def fix_twodevaiacquirer():
    filepath = r"IO.NI\TwoDeviceAiAcquirer.cs"
    
    with open(filepath, 'r', encoding='utf-8-sig') as f:
        content = f.read()
    
    # 1. 替换 DevClock 类定义和 _devClocks 字段
    pattern1 = r'(\s+// 顶部字段声明\s+private sealed class DevClock[^}]+}\s+private readonly ConcurrentDictionary<string, DevClock> _devClocks = new\(\);)'
    replacement1 = '''        // —— 统一时间基准(不再按设备分开，避免长时间漂移)—— //
        private DateTime _globalLast = DateTime.Now;
        private readonly object _globalLastLock = new object();'''
    
    content = re.sub(pattern1, replacement1, content, flags=re.DOTALL)
    
    # 2. 替换 Start() 方法中的设备时钟初始化
    pattern2 = r'(\s+// 为每个实际启用的设备放入独立的时钟\s+if \(_dev1Channels\.Length > 0\)\s+_devClocks\["Dev1"\][^;]+;\s+if \(_dev2Channels\.Length > 0\)\s+_devClocks\["Dev2"\][^;]+;)'
    replacement2 = '''            // —— 移除按设备分开的时钟初始化(改用全局统一时间基准)—— //
            lock (_globalLastLock)
            {
                _globalLast = _t0;
            }'''
    
    content = re.sub(pattern2, replacement2, content, flags=re.DOTALL)
    
    # 3. 替换 OnAiBatch 中取设备时钟的逻辑
    pattern3 = r'(\s+// ② 取本设备的时钟状态\s+if \(!_devClocks\.TryGetValue\(device, out var clk\)\)[^}]+}\s+var last = clk\.Last;\s+//  两种时间[^\n]+\n\s+var hostNow = _t0\.AddMilliseconds\(_sw\.ElapsedMilliseconds - _ts0\);[^\n]+\n\s+var idealNow = last\.AddSeconds\(n / _sampleRate\);[^\n]+\n\s+// ③ 轻微纠偏[^\n]+\n\s+var driftMs = \(hostNow - idealNow\)\.TotalMilliseconds;\s+var current = Math\.Abs\(driftMs\) > 5 \? hostNow : idealNow;)'
    replacement3 = '''                // ② 使用全局时钟(所有设备统一时间基准)
                DateTime last;
                DateTime current;
                double driftMs;
                lock (_globalLastLock)
                {
                    last = _globalLast;
                    
                    //  两种时间：主机"实测" + 按采样率推进的"理想"
                    var hostNow = _t0.AddMilliseconds(_sw.ElapsedMilliseconds - _ts0);
                    var idealNow = last.AddSeconds(n / _sampleRate);
                    
                    // ③ 轻微纠偏(例如 >5ms 时用主机时间，否则用理想时间，避免长期漂移)
                    driftMs = (hostNow - idealNow).TotalMilliseconds;
                    current = Math.Abs(driftMs) > 5 ? hostNow : idealNow;
                    
                    _globalLast = current;
                }'''
    
    content = re.sub(pattern3, replacement3, content, flags=re.DOTALL)
    
    # 4. 替换更新设备时钟的代码
    pattern4 = r'(\s+// ⑧ 更新本设备的时钟\s+clk\.Last = current;\s+clk\.Samples \+= n;)'
    replacement4 = '''                // ⑧ 时钟更新已在上面的 lock 块中完成'''
    
    content = re.sub(pattern4, replacement4, content)
    
    # 写回文件
    with open(filepath, 'w', encoding='utf-8-sig', newline='\r\n') as f:
        f.write(content)
    
    print("修改完成！")

if __name__ == "__main__":
    fix_twodevaiacquirer()
