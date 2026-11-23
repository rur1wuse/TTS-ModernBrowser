# Installs TtsModernBrowser into the Tabletop Simulator folder.
param(
    [string]$GameDir = "E:\SteamLibrary\steamapps\common\Tabletop Simulator",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$dist = Join-Path $PSScriptRoot "dist"
$pluginSrc = Join-Path $dist "BepInEx\plugins\TtsModernBrowser"
$pluginDst = Join-Path $GameDir "BepInEx\plugins\TtsModernBrowser"

if (-not (Test-Path (Join-Path $GameDir "Tabletop Simulator.exe"))) {
    throw "Tabletop Simulator.exe not found in '$GameDir'. Pass -GameDir <path>."
}

if (-not (Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.dll"))) {
    throw "BepInEx is not installed in '$GameDir'. Install BepInEx 5.4.23.6 (win_x64) first."
}

if (-not (Test-Path $pluginSrc)) {
    throw "Build output missing at '$pluginSrc'. Run: dotnet build src\TtsModernBrowser\TtsModernBrowser.csproj -c Release"
}

$running = Get-Process -Name "Tabletop Simulator" -ErrorAction SilentlyContinue
if ($running -and -not $Force) {
    throw "Tabletop Simulator is running; close it before installing."
}

New-Item -ItemType Directory -Force -Path $pluginDst | Out-Null
Copy-Item -Path (Join-Path $pluginSrc "*") -Destination $pluginDst -Recurse -Force
New-Item -ItemType Directory -Force -Path (Join-Path $pluginDst "WebView2Data") | Out-Null

# Ship the usage guide next to the plugin so it is readable without the repo.
$readme = Join-Path $PSScriptRoot "README.md"
if (Test-Path $readme) {
    Copy-Item -Path $readme -Destination (Join-Path $pluginDst "README.md") -Force
}

Write-Host "Installed plugin to $pluginDst"
Write-Host "Guide:   $(Join-Path $pluginDst 'README.md')"
Write-Host "Config:  $(Join-Path $GameDir 'BepInEx\config\com.tts.modernbrowser.cfg')"
Write-Host "Log:     $(Join-Path $GameDir 'BepInEx\LogOutput.log')"
Write-Host "Profile: $(Join-Path $pluginDst 'WebView2Data')"
