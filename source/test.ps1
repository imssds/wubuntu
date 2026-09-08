param([Parameter(Mandatory=$true)][string]$ScratchDirectory, [switch]$LiveCheck)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$arguments = @('/nologo','/target:exe','/platform:x64','/main:Wubuntu.Tests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',"/out:$(Join-Path $ScratchDirectory 'Wubuntu.Tests.exe')","/resource:$(Join-Path $ScratchDirectory 'Wubuntu.ico'),Wubuntu.ico")
$arguments += "/win32manifest:$(Join-Path $PSScriptRoot 'Wubuntu.manifest')"
foreach ($name in @('starting','running','restarting','stopping','error')) { $arguments += "/resource:$(Join-Path $ScratchDirectory ($name + '.png')),$name.png" }
foreach ($name in @('Core.cs','WslBackend.cs','TrayApp.cs','Tests.cs')) { $arguments += Join-Path $PSScriptRoot $name }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
$testArguments = @($ScratchDirectory)
if ($LiveCheck) { $testArguments += '--live-check' }
& (Join-Path $ScratchDirectory 'Wubuntu.Tests.exe') @testArguments
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
