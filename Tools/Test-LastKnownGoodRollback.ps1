param([string]$InstallerPath = (Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'))
$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$source = [IO.File]::ReadAllText($InstallerPath, [Text.Encoding]::UTF8)
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$branch = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Clauses[0].Item1.Extent.Text -eq "`$Mode -eq 'PromoteLastKnownGood'"
}, $true)
if ($null -eq $branch) { throw 'Promotion branch not found' }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('EpbLkgTests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
foreach ($hasOld in @($true, $false)) {
foreach ($failureStage in @('staging', 'published', 'none', 'retired-missing', 'occupied', 'retired-occupied')) {
    if ($failureStage -in @('retired-missing', 'occupied', 'retired-occupied') -and -not $hasOld) { continue }
    $root = Join-Path $fixture ("$hasOld-$failureStage")
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Current'))
    [IO.File]::WriteAllText((Join-Path $root 'Current\payload.txt'), 'new')
    if ($hasOld) {
        [void][IO.Directory]::CreateDirectory((Join-Path $root 'LastKnownGood'))
        [IO.File]::WriteAllText((Join-Path $root 'LastKnownGood\payload.txt'), 'old')
    }
    & {
        param($root, $failureStage, $body, $hasOld)
        $Mode = 'PromoteLastKnownGood'
        $serviceName = 'FixtureOnly'
        $script:started = $false
        $script:archived = $false
        function Assert-InstalledMainStopped { }
        function Enter-DeploymentMaintenance { }
        function Stop-Supervisor { }
        function Assert-RequiredProgramFiles { }
        function Start-Service { $script:started = $true }
        function Archive-MaintenanceInhibitForInstall { $script:archived = $true }
        if ($failureStage -in @('occupied', 'retired-occupied')) {
            # 保留实际 Directory.Move，仅在旧槽完成退役后注入外部占位。
            $retireCall = '[IO.Directory]::Move($oldLkgItem.FullName, $retired)'
            if ([regex]::Matches($body, [regex]::Escape($retireCall)).Count -ne 1) {
                throw 'FixtureRetirementInjectionPointChanged'
            }
            if ($failureStage -eq 'retired-occupied') {
                $body = $body.Replace($retireCall, @'
[void][IO.Directory]::CreateDirectory($retired)
            [IO.File]::WriteAllText((Join-Path $retired 'external.txt'), 'external')
'@ + "`n" + $retireCall)
            } else {
                $body = $body.Replace($retireCall, $retireCall + @'

            [void][IO.Directory]::CreateDirectory($lkg)
            [IO.File]::WriteAllText((Join-Path $lkg 'payload.txt'), 'external')
'@)
            }
        }
        function Get-VerifiedDeploymentIdentity([string]$Directory) {
            $leaf = [IO.Path]::GetFileName($Directory)
            if ($failureStage -eq 'retired-missing' -and $leaf -eq 'LastKnownGood') {
                $old = @(Get-ChildItem -LiteralPath $root -Directory -Filter '.lkg-retired-*')
                if ($old.Count -ne 1) { throw 'FixtureRetiredNotFound' }
                # 在夹具内部移走但不删除旧基准，模拟回退时原路径失踪。
                Move-Item -LiteralPath $old[0].FullName -Destination (Join-Path $root 'held-old-evidence')
                throw 'InjectedIdentityFailure'
            }
            if (($failureStage -eq 'staging' -and $leaf.StartsWith('.lkg-staging-')) -or
                ($failureStage -eq 'published' -and $leaf -eq 'LastKnownGood')) {
                throw 'InjectedIdentityFailure'
            }
        }
        $caught = $null
        try { & ([scriptblock]::Create('[CmdletBinding(SupportsShouldProcess=$true)]param()' +
            [Environment]::NewLine + $body)) -Confirm:$false }
        catch { $caught = $_.Exception.Message }
        $actual = if (Test-Path -LiteralPath (Join-Path $root 'LastKnownGood\payload.txt')) {
            [IO.File]::ReadAllText((Join-Path $root 'LastKnownGood\payload.txt'))
        } else { $null }
        if ($failureStage -eq 'retired-occupied') {
            $occupied = @(Get-ChildItem -LiteralPath $root -Directory -Filter '.lkg-retired-*')
            $pending = @(Get-ChildItem -LiteralPath $root -Directory -Filter '.lkg-staging-*')
            if (-not $caught -or $actual -ne 'old' -or $script:started -or $script:archived -or
                $occupied.Count -ne 1 -or $pending.Count -ne 1) {
                throw "Retirement collision did not preserve original slot: $caught"
            }
            if (@(Get-ChildItem -LiteralPath $occupied[0].FullName -Force).Count -ne 1 -or
                [IO.File]::ReadAllText((Join-Path $occupied[0].FullName 'external.txt')) -ne 'external' -or
                [IO.File]::ReadAllText((Join-Path $pending[0].FullName 'payload.txt')) -ne 'new') {
                throw 'Retirement merged into occupied target or lost candidate'
            }
        } elseif ($failureStage -eq 'occupied') {
            if ($caught -notlike '*Publication=*Rollback=LastKnownGoodRollbackDestinationOccupied*' -or
                $actual -ne 'external' -or $script:started -or $script:archived) {
                throw "Occupied destination was changed or not reported: $caught"
            }
            $old = @(Get-ChildItem -LiteralPath $root -Directory -Filter '.lkg-retired-*')
            $pending = @(Get-ChildItem -LiteralPath $root -Directory -Filter '.lkg-staging-*')
            if ($old.Count -ne 1 -or $pending.Count -ne 1 -or
                [IO.File]::ReadAllText((Join-Path $old[0].FullName 'payload.txt')) -ne 'old' -or
                [IO.File]::ReadAllText((Join-Path $pending[0].FullName 'payload.txt')) -ne 'new') {
                throw 'Collision lost original or candidate evidence'
            }
        } elseif ($failureStage -eq 'retired-missing') {
            if ($caught -notlike '*Publication=InjectedIdentityFailure*Rollback=LastKnownGoodRetiredMissing*' -or
                $script:started -or $script:archived -or
                (Test-Path -LiteralPath (Join-Path $root 'LastKnownGood')) -or
                [IO.File]::ReadAllText((Join-Path $root 'held-old-evidence\payload.txt')) -ne 'old') {
                throw "Missing retired evidence was not reported correctly: $caught"
            }
            $failed = @(Get-ChildItem -LiteralPath $root -Directory -Filter '.lkg-failed-*')
            if ($failed.Count -ne 1 -or
                [IO.File]::ReadAllText((Join-Path $failed[0].FullName 'payload.txt')) -ne 'new') {
                throw 'Failed candidate lost during rollback failure'
            }
        } elseif ($failureStage -eq 'none') {
            if ($caught -or $actual -ne 'new' -or -not $script:started -or -not $script:archived) {
                throw "Successful promotion failed: $caught"
            }
        } else {
            $expected = if ($hasOld) { 'old' } else { $null }
            if ($caught -ne 'InjectedIdentityFailure' -or $actual -ne $expected -or
                $script:started -or $script:archived) { throw "Rollback failed: $caught" }
            if (-not $hasOld -and (Test-Path -LiteralPath (Join-Path $root 'LastKnownGood'))) {
                throw 'Failed first promotion left a formal slot'
            }
            $preserved = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object {
                $_.Name -like '.lkg-staging-*' -or $_.Name -like '.lkg-failed-*'
            })
            if ($preserved.Count -ne 1 -or
                [IO.File]::ReadAllText((Join-Path $preserved[0].FullName 'payload.txt')) -ne 'new') {
                throw 'Candidate evidence not preserved'
            }
        }
        Write-Output "PASS HasOld=$hasOld Stage=$failureStage"
    } $root $failureStage $branch.Extent.Text $hasOld
}
}
foreach ($invalidKind in @('file', 'junction')) {
& {
    param($fixture, $body, $invalidKind)
    $root = Join-Path $fixture ("invalid-old-$invalidKind")
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'Current'))
    [IO.File]::WriteAllText((Join-Path $root 'Current\payload.txt'), 'new')
    if ($invalidKind -eq 'file') {
        [IO.File]::WriteAllText((Join-Path $root 'LastKnownGood'), 'not-a-directory')
    } else {
        $linkedTarget = Join-Path $root 'preserved-link-target'
        [void][IO.Directory]::CreateDirectory($linkedTarget)
        [IO.File]::WriteAllText((Join-Path $linkedTarget 'payload.txt'), 'linked-old')
        [void](New-Item -ItemType Junction -Path (Join-Path $root 'LastKnownGood') -Target $linkedTarget)
    }
    $Mode = 'PromoteLastKnownGood'
    function Assert-InstalledMainStopped { }
    function Enter-DeploymentMaintenance { }
    function Stop-Supervisor { }
    function Assert-RequiredProgramFiles { }
    function Get-VerifiedDeploymentIdentity { }
    function Start-Service { throw 'Must not start service' }
    function Archive-MaintenanceInhibitForInstall { throw 'Must not clear maintenance' }
    $caught = $null
    try { & ([scriptblock]::Create('[CmdletBinding(SupportsShouldProcess=$true)]param()' +
        [Environment]::NewLine + $body)) -Confirm:$false }
    catch { $caught = $_.Exception.Message }
    if ($caught -ne 'LastKnownGoodOriginalTargetInvalid' -or
        @(Get-ChildItem -LiteralPath $root -Filter '.lkg-retired-*').Count -ne 0) {
        throw "Invalid old target was moved or accepted: $caught"
    }
    if ($invalidKind -eq 'file') {
        if ([IO.File]::ReadAllText((Join-Path $root 'LastKnownGood')) -ne 'not-a-directory') {
            throw 'Original file changed'
        }
    } else {
        $link = Get-Item -LiteralPath (Join-Path $root 'LastKnownGood') -Force
        if (($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or
            [IO.File]::ReadAllText((Join-Path $linkedTarget 'payload.txt')) -ne 'linked-old') {
            throw 'Original junction or target changed'
        }
    }
    Write-Output "PASS invalid old $invalidKind preserved before retirement"
} $fixture $branch.Extent.Text $invalidKind
}
Write-Output "PASS 11/11; SyntheticOnly=true; InstallationPerformed=false; PreservedFixture=$fixture"
