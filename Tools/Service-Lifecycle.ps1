#requires -Version 5.1
# Poll the actual SCM transition within one deadline. A CIM snapshot is not a
# lease: the service can finish starting/stopping before the control request.
function Set-ServiceControllerStateBounded {
    param([Parameter(Mandatory=$true)]$Controller,
        [ValidateSet('Running','Stopped')][string]$Target,
        [ValidateRange(1,60000)][int]$TimeoutMilliseconds=15000)
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        $Controller.Refresh()
        $status=[string]$Controller.Status
        if($status -eq $Target){return}
        if($clock.ElapsedMilliseconds -ge $TimeoutMilliseconds){throw ('Service transition deadline: '+$Controller.ServiceName+' -> '+$Target+'; current='+$status)}
        try{
            if($Target -eq 'Stopped' -and $status -in @('Running','Paused') -and $Controller.CanStop){$Controller.Stop()}
            elseif($Target -eq 'Running' -and $status -eq 'Stopped'){$Controller.Start()}
        }catch{
            $native=$_.Exception.GetBaseException()
            # SCM can complete a pending transition between Refresh and control.
            # Access denied and other failures remain visible immediately.
            $transient=$native -is [ComponentModel.Win32Exception] -and
                (($Target -eq 'Stopped' -and $native.NativeErrorCode -in @(1061,1062)) -or
                 ($Target -eq 'Running' -and $native.NativeErrorCode -in @(1056,1061)))
            if(-not $transient){throw}
        }
        Start-Sleep -Milliseconds ([Math]::Min(100,[Math]::Max(1,$TimeoutMilliseconds-$clock.ElapsedMilliseconds)))
    }
}