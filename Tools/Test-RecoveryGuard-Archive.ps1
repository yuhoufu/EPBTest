#requires -Version 5.1
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'RecoveryGuard-Archive.ps1')
Import-Module Microsoft.PowerShell.Archive -ErrorAction Stop
$root=Join-Path ([IO.Path]::GetTempPath()) ('GuardArchiveFixture-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $root)
$passed=0
function Compress-Archive {
    param($Path,$DestinationPath)
    if($script:case -eq 'CompressionFailure') {
        [IO.File]::WriteAllText($DestinationPath,'injected partial ZIP')
        throw 'InjectedCompressionFailure'
    }
    Microsoft.PowerShell.Archive\Compress-Archive -Path $Path -DestinationPath $DestinationPath
    if($script:case -eq 'CorruptArchive'){[IO.File]::WriteAllText($DestinationPath,'injected invalid ZIP')}
    if($script:case -eq 'SourceChanged'){[IO.File]::WriteAllText((Join-Path $script:staging 'one.txt'),'changed after archive')}
    if($script:case -eq 'OutputAppeared'){[IO.File]::WriteAllText(($script:output+'.zip'),'someone else owns this output')}
}
try {
    foreach($script:case in @('Valid','CompressionFailure','CorruptArchive','SourceChanged','OutputAppeared')) {
        $parent=Join-Path $root $case
        $script:staging=Join-Path $parent 'staging'
        $script:output=Join-Path $parent 'published'
        [void](New-Item -ItemType Directory -Path (Join-Path $staging 'nested') -Force)
        [IO.File]::WriteAllText((Join-Path $staging 'one.txt'),'synthetic archive fixture')
        [IO.File]::WriteAllText((Join-Path $staging 'nested\two.txt'),'second synthetic file')
        $rejected=$false
        try {$result=Publish-GuardArchive $staging $output} catch {$rejected=$true;$reason=$_.Exception.Message}
        if($case -eq 'Valid') {
            if($rejected -or $result.FileCount -ne 2 -or -not (Test-Path ($output+'.zip')) -or (Test-Path $staging)){throw "Valid publish failed: $reason"}
        } else {
            if(-not $rejected -or (Test-Path $output) -or -not (Test-Path $staging)){throw "Failure published directory: $case"}
            if($case -ne 'OutputAppeared' -and (Test-Path ($output+'.zip'))){throw "Partial public ZIP exists: $case"}
            if($case -eq 'OutputAppeared' -and [IO.File]::ReadAllText($output+'.zip') -ne 'someone else owns this output'){throw 'Existing output overwritten'}
        }
        if(@(Get-ChildItem -LiteralPath $parent -Filter '.automatic-archive-*.zip' -File -Force).Count){throw 'Temporary ZIP remained'}
        $passed++; Write-Output "PASS $case"
    }
    Write-Output "PASS GuardArchive $passed/$passed; synthetic text files; no installation"
} finally {
    Remove-Item Function:\Compress-Archive
    Write-Output "FixtureDirectory=$root"
}
