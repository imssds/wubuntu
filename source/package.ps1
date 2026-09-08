param([string]$ScratchDirectory)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
$version = & (Join-Path $PSScriptRoot 'get-version.ps1')
$staging = Join-Path ([IO.Path]::GetTempPath()) ('Wubuntu-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    & (Join-Path $PSScriptRoot 'build.ps1') -ScratchDirectory $ScratchDirectory -OutputDirectory $staging
    Copy-Item -LiteralPath (Join-Path $appRoot 'docs/QUICKSTART.txt') -Destination $staging
    $files = @('Wubuntu.exe', 'Wubuntu.exe.config', 'LICENSE', 'QUICKSTART.txt') |
        ForEach-Object { Join-Path $staging $_ }
    $dist = Join-Path $appRoot 'dist'
    New-Item -ItemType Directory -Path $dist -Force | Out-Null
    $archive = Join-Path $dist "Wubuntu-$version-win-x64.zip"
    Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
    Write-Output "Packaged $archive"
} finally {
    # Delete only files created by this package build; leave unexpected files alone.
    foreach ($name in @('Wubuntu.exe', 'Wubuntu.exe.config', 'Wubuntu.ico', 'LICENSE', 'QUICKSTART.txt')) {
        $file = Join-Path $staging $name
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file }
    }
    Remove-Item -LiteralPath $staging
}
