param([string]$Tag)
$ErrorActionPreference = 'Stop'
$version = (Get-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'VERSION') -Raw).Trim()
if ($version -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'VERSION must contain MAJOR.MINOR.PATCH without leading zeros.'
}
foreach ($part in $version.Split('.')) {
    if ([double]$part -gt 65534) { throw 'Version components must be between 0 and 65534 for Windows assembly versions.' }
}
if ($Tag -and $Tag -cne "v$version") { throw "Tag '$Tag' does not match VERSION '$version'." }
$version
