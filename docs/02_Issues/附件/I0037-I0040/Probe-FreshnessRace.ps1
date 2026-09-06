param(
    [string]$ReleaseDirectory = 'D:\Github\wanxiang\EPBTest\artifacts\releases\V2.16.0.0-343ea80f8331-20260905_073154',
    [string]$OutputPath = (Join-Path $PSScriptRoot 'freshness-race-repro.json')
)
$ErrorActionPreference = 'Stop'
# Read-only production-assembly probe. No DAQ task, application, service, or
# power-supply object is created. The two cases model a legal read interleaving.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
public static class FreshnessRaceProbe {
    const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static object Get(object o, string n) { return o.GetType().GetProperty(n, Flags).GetValue(o, null); }
    static void Set(object o, string n, object v) { o.GetType().GetProperty(n, Flags).SetValue(o, v, null); }
    public static object[] Run(string dir) {
        var ni = Assembly.LoadFrom(Path.Combine(dir, "IO.NI.dll"));
        var controller = Assembly.LoadFrom(Path.Combine(dir, "Controller.dll"));
        var mt = ni.GetType("IO.NI.FastControlBatchMetadata", true);
        ConstructorInfo ctor = null;
        foreach (var c in mt.GetConstructors(Flags)) if(c.GetParameters().Length == 17) ctor = c;
        var p = ctor.GetParameters();
        long now = Stopwatch.Frequency * 10L;
        long capture = now - Stopwatch.Frequency / 500;
        var batch = ctor.Invoke(new object[] { 1L, 100L, DateTime.UtcNow, DateTime.UtcNow,
            capture, capture, 0.0, 1000.0, Enum.ToObject(p[8].ParameterType, 0),
            0.0, 0.0, 1.0, 1, Enum.ToObject(p[13].ParameterType, 0), 0, 10,
            Enum.Parse(p[16].ParameterType, "Healthy") });
        var pt = ni.GetType("IO.NI.DaqControlPublication", true);
        var publication = Activator.CreateInstance(pt, Flags, null, new object[] { batch, now + 1 }, null);
        var results = new object[2];
        for (int i = 0; i < 2; i++) {
            var snapshot = Activator.CreateInstance(ni.GetType("IO.NI.DaqFreshnessSnapshot", true));
            Set(snapshot, "CallbackAgeMs", 0.5);
            Set(snapshot, "ControlEnqueueAgeMs", 0.5);
            Set(snapshot, "LastProducedSequence", 100L);
            pt.GetMethod("Apply", Flags).Invoke(publication,
                new object[] { snapshot, 1L, now + (i == 0 ? 0 : 2), 100.0 });
            var state = Activator.CreateInstance(controller.GetType("Controller.DaqLivenessDeviceState", true), true);
            var decision = state.GetType().GetMethod("Observe", Flags).Invoke(state,
                new object[] { true, true, false, snapshot, 75.0, 100.0, 250.0 });
            results[i] = new {
                Case = i == 0 ? "PublicationReadAfterNow" : "NowReadAfterPublication",
                TickFrequency = Stopwatch.Frequency,
                IsFresh = Get(snapshot, "IsFresh"),
                CallbackAgeMs = Get(snapshot, "CallbackAgeMs"),
                SampleAgeMs = Get(snapshot, "SampleAgeMs"),
                ControlProcessedAgeMs = Get(snapshot, "ControlProcessedAgeMs").ToString(),
                RejectionReason = Get(snapshot, "RejectionReason"),
                Trip = Get(decision, "Trip"), Code = Get(decision, "Code"),
                Reason = Get(decision, "Reason")
            };
        }
        return results;
    }
}
'@
$result = [ordered]@{
    Purpose = 'Deterministic interleaving reproduction using unchanged V2.16 release assemblies; no field hardware used.'
    ReleaseDirectory = $ReleaseDirectory
    IoNiSha256 = (Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory 'IO.NI.dll') -Algorithm SHA256).Hash
    ControllerSha256 = (Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory 'Controller.dll') -Algorithm SHA256).Hash
    Cases = @([FreshnessRaceProbe]::Run($ReleaseDirectory))
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json -Depth 8
if (-not $result.Cases[0].Trip -or $result.Cases[1].Trip) { throw 'Unexpected reproduction result.' }
