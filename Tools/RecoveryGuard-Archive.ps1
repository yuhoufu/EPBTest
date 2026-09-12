#requires -Version 5.1
function Publish-GuardArchive {
    param([Parameter(Mandatory=$true)][string]$StagingDirectory,
        [Parameter(Mandatory=$true)][string]$OutputDirectory)
    $staging=[IO.Path]::GetFullPath($StagingDirectory).TrimEnd('\')
    $output=[IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
    $parent=[IO.Path]::GetDirectoryName($output)
    if ([IO.Path]::GetDirectoryName($staging) -ine $parent -or $staging -ieq $output -or
        -not (Test-Path -LiteralPath $staging -PathType Container)) { throw 'ArchiveStagingOutsideParent' }
    if ((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output+'.zip'))) { throw 'ArchiveOutputExists' }
    $expected=New-Object 'System.Collections.Generic.Dictionary[string,string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($file in Get-ChildItem -LiteralPath $staging -File -Recurse -Force) {
        $expected.Add($file.FullName.Substring($staging.Length+1).Replace('\','/'), (Get-FileHash -LiteralPath $file.FullName).Hash)
    }
    if($expected.Count -eq 0){throw 'ArchiveInputEmpty'}
    $temporaryZip=Join-Path $parent ('.automatic-archive-'+[Guid]::NewGuid().ToString('N')+'.zip')
    try {
        Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $temporaryZip
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive=[IO.Compression.ZipFile]::OpenRead($temporaryZip)
        try {
            $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
            foreach($entry in $archive.Entries) {
                if($entry.FullName.EndsWith('/')){continue}
                $name=$entry.FullName.Replace('\','/')
                if(-not $seen.Add($name) -or -not $expected.ContainsKey($name)){throw 'ArchiveFileSetMismatch'}
                $stream=$entry.Open(); $algorithm=[Security.Cryptography.SHA256]::Create()
                try {$hash=[BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}
                finally {$stream.Dispose();$algorithm.Dispose()}
                if($hash -ine $expected[$name]){throw 'ArchiveFileHashMismatch'}
            }
            if($seen.Count -ne $expected.Count){throw 'ArchiveFileSetMismatch'}
        } finally {$archive.Dispose()}
        # Verify the directory also still agrees with the archive immediately before publishing.
        $files=@(Get-ChildItem -LiteralPath $staging -File -Recurse -Force)
        if($files.Count -ne $expected.Count){throw 'ArchiveSourceChanged'}
        foreach($file in $files) {
            $name=$file.FullName.Substring($staging.Length+1).Replace('\','/')
            if(-not $expected.ContainsKey($name) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ine $expected[$name]){throw 'ArchiveSourceChanged'}
        }
        if((Test-Path -LiteralPath $output) -or (Test-Path -LiteralPath ($output+'.zip'))){throw 'ArchiveOutputAppeared'}
        # Both paths are resolved siblings. These moves reject existing destinations.
        [IO.Directory]::Move($staging,$output)
        [IO.File]::Move($temporaryZip,($output+'.zip'))
        return [pscustomobject]@{Directory=$output;Archive=($output+'.zip');Sha256=(Get-FileHash ($output+'.zip')).Hash;FileCount=$expected.Count}
    } finally {
        if([IO.File]::Exists($temporaryZip)){[IO.File]::Delete($temporaryZip)}
    }
}
