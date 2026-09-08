param([string]$ScratchDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
$version = & (Join-Path $PSScriptRoot 'get-version.ps1')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $appRoot 'dist' }
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$temporaryScratch = -not $ScratchDirectory
if ($temporaryScratch) { $ScratchDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Wubuntu-build-' + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $ScratchDirectory -Force | Out-Null
$builder = Join-Path $ScratchDirectory 'AssetBuilder.exe'
try {
$assemblyInfo = Join-Path $ScratchDirectory 'Version.cs'
@"
[assembly: System.Reflection.AssemblyVersion("$version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$version.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("$version")]
"@ | Set-Content -LiteralPath $assemblyInfo -Encoding UTF8
$manifest = Join-Path $ScratchDirectory 'Wubuntu.manifest'
(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Wubuntu.manifest') -Raw).Replace('$VERSION$', "$version.0") |
    Set-Content -LiteralPath $manifest -Encoding UTF8
& $compiler /nologo /target:exe /r:System.Drawing.dll "/out:$builder" (Join-Path $PSScriptRoot 'AssetBuilder.cs')
if ($LASTEXITCODE -ne 0) { throw 'Asset builder compilation failed.' }
& $builder (Join-Path $appRoot 'assets') $ScratchDirectory
if ($LASTEXITCODE -ne 0) { throw 'Asset conversion failed.' }
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',"/win32manifest:$manifest","/win32icon:$(Join-Path $ScratchDirectory 'Wubuntu.ico')","/resource:$(Join-Path $ScratchDirectory 'Wubuntu.ico'),Wubuntu.ico","/out:$(Join-Path $outputDirectory 'Wubuntu.exe')",$assemblyInfo)
foreach ($name in @('starting','running','restarting','stopping','error')) { $arguments += "/resource:$(Join-Path $ScratchDirectory ($name + '.png')),$name.png" }
foreach ($name in @('Core.cs','ProcessRunner.cs','WslBackend.cs','TrayApp.cs')) { $arguments += Join-Path $PSScriptRoot $name }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Wubuntu compilation failed.' }
Copy-Item -LiteralPath (Join-Path $ScratchDirectory 'Wubuntu.ico') -Destination (Join-Path $outputDirectory 'Wubuntu.ico') -Force
Copy-Item -LiteralPath (Join-Path $appRoot 'Wubuntu.exe.config') -Destination (Join-Path $outputDirectory 'Wubuntu.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $appRoot 'LICENSE') -Destination (Join-Path $outputDirectory 'LICENSE') -Force
Write-Output "Built $(Join-Path $outputDirectory 'Wubuntu.exe')"
} finally {
if ($temporaryScratch) {
    foreach ($fileName in @('Version.cs','Wubuntu.manifest','AssetBuilder.exe','Wubuntu.ico','starting.png','running.png','restarting.png','stopping.png','error.png')) {
        $scratchFile = Join-Path $ScratchDirectory $fileName
        if (Test-Path -LiteralPath $scratchFile) { Remove-Item -LiteralPath $scratchFile }
    }
    Remove-Item -LiteralPath $ScratchDirectory
}
}
