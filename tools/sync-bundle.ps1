# Copies the freshly built UI bundle from Bundle\Assets\StreamingAssets\
# into src\Resources\gdc_ui.unity3d so the next dotnet build embeds the
# current art. Run this after every "Tools > Build Kumiho UI Bundle" in
# Unity, before rebuilding the plugin DLL.
#
# Usage (from anywhere):
#   pwsh C:\Dev\GDCplugin\tools\sync-bundle.ps1
# Or with a relative invocation:
#   cd C:\Dev\GDCplugin; .\tools\sync-bundle.ps1

$ErrorActionPreference = "Stop"

# Resolve paths relative to this script's location so the script works no
# matter where it's invoked from.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir

# The Unity build script still writes to the original kumiho_ui.unity3d
# filename. I rename on copy to gdc_ui.unity3d so the plugin's embedded
# resource name matches what Plugin.cs expects.
$sourceFile = Join-Path $projectRoot "Bundle\Assets\StreamingAssets\kumiho_ui.unity3d"
$targetFile = Join-Path $projectRoot "src\Resources\gdc_ui.unity3d"

if (-not (Test-Path $sourceFile)) {
    Write-Error "Bundle output not found at: $sourceFile`nDid you run 'Tools > Build Kumiho UI Bundle' in Unity first?"
    exit 1
}

$sourceInfo = Get-Item $sourceFile
Copy-Item -Path $sourceFile -Destination $targetFile -Force

$targetInfo = Get-Item $targetFile
$sizeKB = [math]::Round($targetInfo.Length / 1KB, 1)

Write-Host "Synced bundle:" -ForegroundColor Green
Write-Host "  From: $sourceFile" -ForegroundColor Gray
Write-Host "  To:   $targetFile" -ForegroundColor Gray
Write-Host "  Size: $sizeKB KB, modified $($sourceInfo.LastWriteTime)" -ForegroundColor Gray
Write-Host ""
Write-Host "Next: dotnet build .\src\GDCplugin.csproj -c Release" -ForegroundColor Cyan
