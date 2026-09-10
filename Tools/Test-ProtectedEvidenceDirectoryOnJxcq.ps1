$ErrorActionPreference = 'Stop'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Install-MTTFTest-Unattended.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$definitions = @('Assert-DeploymentEvidenceDirectory', 'Initialize-ProtectedDeploymentDirectory', 'Assert-DeploymentTaskBackupEntries', 'Read-DeploymentTaskBackup', 'Backup-InstalledRuntimeTasks', 'Write-DeploymentTaskPreparation') | ForEach-Object {
    $name = $_
    $definition = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if ($null -eq $definition) { throw "Missing function: $name" }
    $definition.Extent.Text
}
Invoke-Command -ComputerName 'MT-20251206JXCQ' -SessionOption (New-PSSessionOption -OpenTimeout 10000) -ArgumentList ($definitions -join [Environment]::NewLine) -ScriptBlock {
    param($source)
    $ErrorActionPreference = 'Stop'
    . ([scriptblock]::Create($source))
    if ($env:COMPUTERNAME -ne 'MT-20251206JXCQ') { throw 'Wrong validation host' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Elevated validation required' }
    $parent = 'D:\EPB_Validation'
    $item = Get-Item -LiteralPath $parent -Force
    if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Unsafe validation root' }
    $path = Join-Path $parent ('protected-evidence-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $path) { throw 'Unique directory collision' }
    Initialize-ProtectedDeploymentDirectory $path
    Initialize-ProtectedDeploymentDirectory $path
    $actual = Get-Acl -LiteralPath $path
    $rules = @($actual.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if (-not $actual.AreAccessRulesProtected -or $actual.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne 'S-1-5-32-544' -or
        $rules.Count -ne 2) { throw 'Protected directory owner or inheritance mismatch' }
    foreach ($rule in $rules) {
        if ($rule.IdentityReference.Value -notin @('S-1-5-18', 'S-1-5-32-544') -or $rule.IsInherited -or
            $rule.AccessControlType -ne 'Allow' -or $rule.FileSystemRights -ne 'FullControl' -or
            $rule.InheritanceFlags -ne 'ContainerInherit, ObjectInherit' -or $rule.PropagationFlags -ne 'None') {
            throw 'Protected directory ACL mismatch'
        }
    }
    $widePath = Join-Path $parent ('untrusted-evidence-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $widePath)
    $before = (Get-Acl -LiteralPath $widePath).Sddl
    $caught = $null
    try { Initialize-ProtectedDeploymentDirectory $widePath } catch { $caught = $_ }
    if ($null -eq $caught -or $caught.Exception.Message -ne 'ProtectedEvidenceAclMismatch' -or
        (Get-Acl -LiteralPath $widePath).Sddl -cne $before) { throw 'Untrusted existing ACL not rejected unchanged' }
    $originalProgramData = $env:ProgramData
    try {
        $env:ProgramData = $path
        $autoStartTaskName = 'FixtureAuto'
        $taskName = 'FixtureAgent'
        $healthTaskName = 'FixtureHealth'
        $reference = Backup-InstalledRuntimeTasks (Join-Path $path 'FixtureInstall') @()
        $verified = Read-DeploymentTaskBackup $reference.Path $reference.InstallRoot $reference.Sha256
        if ($verified.transactionId -cne $reference.TransactionId) { throw 'Published reference identity changed' }
        $preparationPath = Write-DeploymentTaskPreparation $reference $reference.InstallRoot
        $preparation = Get-Content -LiteralPath $preparationPath -Raw | ConvertFrom-Json
        if ($preparation.phase -cne 'TaskBackupPrepared' -or $preparation.backupSha256 -cne $reference.Sha256 -or
            $preparation.backupPath -cne $reference.Path) { throw 'Preparation did not retain publisher reference' }
        $beforeHash = (Get-FileHash -LiteralPath $preparationPath).Hash
        $duplicate = $null
        try { Write-DeploymentTaskPreparation $reference $reference.InstallRoot | Out-Null } catch { $duplicate = $_ }
        if ($null -eq $duplicate -or (Get-FileHash -LiteralPath $preparationPath).Hash -cne $beforeHash) {
            throw 'Duplicate preparation overwrote original evidence'
        }
        $faultReference = Backup-InstalledRuntimeTasks $reference.InstallRoot @()
        $move = '[IO.File]::Move($pending, $path)'
        if (-not $source.Contains($move)) { throw 'Missing fault injection boundary' }
        . ([scriptblock]::Create($source.Replace($move, $move + [Environment]::NewLine + '[IO.File]::AppendAllText($path, "corrupted")')))
        $corruption = $null
        try { Write-DeploymentTaskPreparation $faultReference $faultReference.InstallRoot | Out-Null } catch { $corruption = $_ }
        if ($null -eq $corruption -or $corruption.Exception.Message -ne 'TaskPreparationReadbackMismatch' -or
            (Get-FileHash -LiteralPath $preparationPath).Hash -cne $beforeHash) { throw 'Preparation corruption not rejected independently' }
        . ([scriptblock]::Create($source))
    } finally { $env:ProgramData = $originalProgramData }
    $backupDirectory = Join-Path $path 'MTTFTestDeploymentEvidence\TaskBackups'
    $backups = @(Get-ChildItem -LiteralPath $backupDirectory -File)
    if ($backups.Count -ne 2) { throw 'Expected original and fault-scenario task backups' }
    $backups = @($backups | Where-Object FullName -eq $reference.Path)
    if ($backups.Count -ne 1) { throw 'Original backup identity missing' }
    $fileAcl = Get-Acl -LiteralPath $backups[0].FullName
    $fileRules = @($fileAcl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($fileRules.Count -ne 2 -or @($fileRules | Where-Object {
        $_.IdentityReference.Value -notin @('S-1-5-18', 'S-1-5-32-544') -or
        $_.AccessControlType -ne 'Allow' -or $_.FileSystemRights -ne 'FullControl'
    }).Count -ne 0) { throw 'Backup file inherited unexpected permissions' }
    $saved = Get-Content -LiteralPath $backups[0].FullName -Raw | ConvertFrom-Json
    if ($saved.schema -ne 4 -or $saved.tasks.Count -ne 3) { throw 'Protected backup content invalid' }
    [pscustomobject]@{ Computer=$env:COMPUTERNAME; Path=$path; CreatedWithProtectedAcl=$true;
        Sddl=$actual.Sddl; ExistingProtectedAccepted=$true; ExistingUntrustedRejected=$true;
        RejectedPath=$widePath; ProtectedBackup=$backups[0].FullName; FileSddl=$fileAcl.Sddl;
        PublisherHashVerified=$true; PreparationPath=$preparationPath; DuplicatePreparationRejected=$true; CorruptPreparationRejected=$true;
        RetainedForAudit=$true; ProductionDirectoryModified=$false }
} | ConvertTo-Json -Depth 4
