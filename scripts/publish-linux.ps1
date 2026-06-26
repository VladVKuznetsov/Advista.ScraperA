# Cross-publish AgentScraper for linux-x64 from a Windows (or any) dev machine.
# This bundles the Linux Playwright driver (.playwright/node/linux-x64/node), which a plain
# Windows build does NOT include — that omission is what causes:
#   PlaywrightException: Driver not found: .../.playwright/node/linux-x64/node
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'bin/Release/net9.0/linux-x64/publish'

dotnet publish (Join-Path $root 'AgentScraper.csproj') -c Release -r linux-x64 --self-contained false

Write-Host ""
Write-Host "Published to: $out"
Write-Host "Driver platforms present:"
Get-ChildItem (Join-Path $out '.playwright/node') | Select-Object -ExpandProperty Name | ForEach-Object { "  - $_" }
Write-Host ""
Write-Host "Next: copy the contents of that folder to the Linux box, then run scripts/setup-linux.sh there."
