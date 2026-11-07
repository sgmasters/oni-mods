# Builds all mods in Release and creates one zip per mod in .\dist, ready to attach to a GitHub release.
# Usage:  powershell -ExecutionPolicy Bypass -File .\package.ps1
#         (optional) -GameDir "D:\Games\OxygenNotIncluded"   if the game is not in the default Epic folder
param([string]$GameDir = "")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

foreach ($mod in @("DupeBrainBoost", "SafeDupes", "DupeTraffic")) {
    $proj = Join-Path $root "$mod\$mod.csproj"
    $stage = Join-Path $dist $mod
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    $buildArgs = @("build", $proj, "-c", "Release", "-p:ModInstallDir=$stage")
    if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }
    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed for $mod" }
    $version = (Select-String -Path (Join-Path $root "$mod\mod_info.yaml") -Pattern 'version:\s*"?([^"]+)"?').Matches[0].Groups[1].Value
    $zip = Join-Path $dist "$mod-$version.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    # The zip contains the mod folder itself, so players extract it straight into mods\local.
    Compress-Archive -Path $stage -DestinationPath $zip
    Write-Host "Packaged $zip"
}
