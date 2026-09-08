$ErrorActionPreference = 'Stop'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Wubuntu-version-test-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $fixture 'source'
New-Item -ItemType Directory -Path $source | Out-Null
$script = Join-Path $source 'get-version.ps1'
$versionFile = Join-Path $fixture 'VERSION'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'get-version.ps1') -Destination $script
try {
    foreach ($version in @('0.0.0', '1.0.0', '12.34.56', '65534.65534.65534')) {
        Set-Content -LiteralPath $versionFile -Value $version
        if ((& $script -Tag "v$version") -cne $version) { throw "Valid version rejected: $version" }
    }
    foreach ($version in @('', '1.0', '1.0.0.0', '01.0.0', '-1.0.0', '1.0.0-beta', '65535.0.0', '999999999999999999999999.0.0')) {
        Set-Content -LiteralPath $versionFile -Value $version
        $rejected = $false
        try { & $script | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Invalid version accepted: $version" }
    }
    Set-Content -LiteralPath $versionFile -Value '1.0.0'
    foreach ($tag in @('v1.0.1', '1.0.0', 'V1.0.0', 'v1.0.0-beta')) {
        $rejected = $false
        try { & $script -Tag $tag | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw "Mismatched tag accepted: $tag" }
    }
    Write-Output 'Version tests passed (16 cases).'
} finally {
    Remove-Item -LiteralPath $script, $versionFile -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $source
    Remove-Item -LiteralPath $fixture
}
