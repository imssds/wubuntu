param(
    [Parameter(Mandatory=$true)][string]$ScratchDirectory,
    [ValidateSet('core','process','ui','all')][string]$Group = 'all',
    [switch]$LiveCheck,
    [switch]$SavePreview
)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:exe "/out:$(Join-Path $ScratchDirectory 'ProcessTestHelper.exe')" (Join-Path $PSScriptRoot 'ProcessTestHelper.cs')
if ($LASTEXITCODE -ne 0) { throw 'Process test helper compilation failed.' }
$arguments = @('/nologo','/target:exe','/platform:x64','/main:Wubuntu.Tests','/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll',"/out:$(Join-Path $ScratchDirectory 'Wubuntu.Tests.exe')","/resource:$(Join-Path $ScratchDirectory 'Wubuntu.ico'),Wubuntu.ico")
$arguments += "/win32manifest:$(Join-Path $ScratchDirectory 'Wubuntu.manifest')"
$arguments += Join-Path $ScratchDirectory 'Version.cs'
foreach ($name in @('starting','running','restarting','stopping','error')) { $arguments += "/resource:$(Join-Path $ScratchDirectory ($name + '.png')),$name.png" }
foreach ($name in @('Core.cs','ProcessRunner.cs','WslBackend.cs','TrayApp.cs','Tests.cs')) { $arguments += Join-Path $PSScriptRoot $name }
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
$testArguments = @($ScratchDirectory, $Group)
if ($LiveCheck) { $testArguments += '--live-check' }
if ($SavePreview) { $testArguments += '--save-preview' }
& (Join-Path $ScratchDirectory 'Wubuntu.Tests.exe') @testArguments 2>&1 |
    Tee-Object -FilePath (Join-Path $ScratchDirectory $(if ($LiveCheck) { 'live-check-tests.log' } else { "$Group-tests.log" }))
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
