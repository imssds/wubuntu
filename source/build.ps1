param([string]$ScratchDirectory)
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $appRoot 'dist'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$temporaryScratch = -not $ScratchDirectory
if ($temporaryScratch) { $ScratchDirectory = Join-Path ([IO.Path]::GetTempPath()) ('Wubuntu-build-' + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Path $ScratchDirectory -Force | Out-Null
$builder = Join-Path $ScratchDirectory 'AssetBuilder.exe'
& $compiler /nologo /target:exe /r:System.Drawing.dll "/out:$builder" (Join-Path $PSScriptRoot 'AssetBuilder.cs')
if ($LASTEXITCODE -ne 0) { throw 'Asset builder compilation failed.' }
& $builder (Join-Path $appRoot 'assets') $ScratchDirectory
if ($LASTEXITCODE -ne 0) { throw 'Asset conversion failed.' }
$arguments = @('/nologo','/target:winexe','/platform:x64','/optimize+','/warn:4','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',"/win32manifest:$(Join-Path $PSScriptRoot 'Wubuntu.manifest')","/win32icon:$(Join-Path $ScratchDirectory 'Wubuntu.ico')","/resource:$(Join-Path $ScratchDirectory 'Wubuntu.ico'),Wubuntu.ico","/out:$(Join-Path $outputDirectory 'Wubuntu.exe')")
foreach ($name in @('starting','running','restarting','stopping','error')) { $arguments += "/resource:$(Join-Path $ScratchDirectory ($name + '.png')),$name.png" }
foreach ($name in @('Core.cs','WslBackend.cs','TrayApp.cs')) { $arguments += Join-Path $PSScriptRoot $name }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Wubuntu compilation failed.' }
Copy-Item -LiteralPath (Join-Path $ScratchDirectory 'Wubuntu.ico') -Destination (Join-Path $outputDirectory 'Wubuntu.ico') -Force
Copy-Item -LiteralPath (Join-Path $appRoot 'Wubuntu.exe.config') -Destination (Join-Path $outputDirectory 'Wubuntu.exe.config') -Force
Copy-Item -LiteralPath (Join-Path $appRoot 'LICENSE') -Destination (Join-Path $outputDirectory 'LICENSE') -Force
Write-Output "Built $(Join-Path $outputDirectory 'Wubuntu.exe')"
if ($temporaryScratch) {
    foreach ($fileName in @('AssetBuilder.exe','Wubuntu.ico','starting.png','running.png','restarting.png','stopping.png','error.png')) {
        Remove-Item -LiteralPath (Join-Path $ScratchDirectory $fileName)
    }
    Remove-Item -LiteralPath $ScratchDirectory
}
