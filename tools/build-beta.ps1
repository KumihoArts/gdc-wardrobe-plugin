# Cuts an obfuscated beta build of GDCplugin and deploys it to HS2.
#
# Normal dev loop stays untouched: `dotnet build -c Release` keeps producing a
# clean, fast DLL. Run THIS script only when handing a build to testers.
#
# Steps:
#   1. Clean Release build (also auto-deploys the clean DLL; overwritten below).
#   2. ConfuserEx2 over bin\GDCplugin.dll -> beta\GDCplugin.dll (conservative,
#      Mono-safe ruleset in tools\GDCplugin.crproj).
#   3. Copy the obfuscated DLL over the deployed one in the HS2 plugins folder.
#
# After running, restart HS2 and confirm the plugin loads AND the UI bundle
# shows (the one thing obfuscation could plausibly break). See tools\GDCplugin.crproj.

$ErrorActionPreference = 'Stop'

$root      = Split-Path $PSScriptRoot -Parent
$csproj    = Join-Path $root 'src\GDCplugin.csproj'
$crproj    = Join-Path $PSScriptRoot 'GDCplugin.crproj'
$confuser  = Join-Path $PSScriptRoot 'confuser\Confuser.CLI.exe'
$cleanDll  = Join-Path $root 'bin\GDCplugin.dll'
$betaDll   = Join-Path $root 'beta\GDCplugin.dll'
$deployDir = 'D:\HS2 R16\BepInEx\Plugins\Kumiho'

foreach ($p in @($csproj, $crproj, $confuser)) {
    if (-not (Test-Path $p)) { throw "Missing required path: $p" }
}

Write-Host '== 1/3  Clean Release build ==' -ForegroundColor Cyan
dotnet build $csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed ($LASTEXITCODE)" }
$cleanSize = [math]::Round((Get-Item $cleanDll).Length / 1MB, 2)

Write-Host '== 2/3  Obfuscate (ConfuserEx2) ==' -ForegroundColor Cyan
& $confuser -n $crproj
if ($LASTEXITCODE -ne 0) { throw "ConfuserEx failed ($LASTEXITCODE)" }
if (-not (Test-Path $betaDll)) { throw "Obfuscated DLL not produced: $betaDll" }
$betaSize = [math]::Round((Get-Item $betaDll).Length / 1MB, 2)

Write-Host '== 3/3  Deploy obfuscated DLL to HS2 ==' -ForegroundColor Cyan
if (-not (Test-Path $deployDir)) { New-Item -ItemType Directory -Force -Path $deployDir | Out-Null }
Copy-Item $betaDll (Join-Path $deployDir 'GDCplugin.dll') -Force

Write-Host ''
Write-Host "Done. clean=$cleanSize MB  obfuscated=$betaSize MB" -ForegroundColor Green
Write-Host "Beta artifact: $betaDll" -ForegroundColor Green
Write-Host "Deployed to:   $deployDir\GDCplugin.dll" -ForegroundColor Green
Write-Host 'Restart HS2. Verify: plugin loads in BepInEx log AND the UI bundle/orbs render (Ctrl+Shift+G).' -ForegroundColor Yellow
