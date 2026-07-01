param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$PackageLabel = "0.5-beta"
$PublishDir = Join-Path $Root "dist\O2CloudDrive-$PackageLabel-win-x64"
$SetupProject = Join-Path $PSScriptRoot "O2CloudDrive.Setup\O2CloudDrive.Setup.csproj"
$UninstallProject = Join-Path $PSScriptRoot "O2CloudDrive.Uninstall\O2CloudDrive.Uninstall.csproj"
$SetupPublishDir = Join-Path $Root "dist\setup-publish"
$UninstallPublishDir = Join-Path $Root "dist\uninstall-publish"
$SetupExe = Join-Path $Root "dist\O2CloudDrive-$PackageLabel-Setup.exe"
$HashFile = Join-Path $Root "dist\O2CloudDrive-$PackageLabel-Setup.sha256.txt"
$Project = Join-Path $Root "src\O2CloudDrive\O2CloudDrive.csproj"
$Dotnet = Join-Path $Root ".dotnet\dotnet.exe"
$Prereqs = Join-Path $PSScriptRoot "prereqs"
$Assets = Join-Path $PSScriptRoot "O2CloudDrive.Setup\Assets"
$PayloadSource = Join-Path $PSScriptRoot "payload-source"
$PayloadZip = Join-Path $Assets "payload.zip"

if (-not (Test-Path -LiteralPath $Dotnet)) {
    $Dotnet = "dotnet"
}

if (-not $SkipPublish) {
    & $Dotnet publish $Project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish de O2CloudDrive fallo."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $PublishDir "O2CloudDrive.exe"))) {
    throw "No se encontro la publicacion de O2CloudDrive en $PublishDir."
}

New-Item -ItemType Directory -Force -Path $Prereqs | Out-Null

$winFspMsi = Join-Path $Prereqs "winfsp-2.1.25156.msi"
if (-not (Test-Path -LiteralPath $winFspMsi) -or (Get-Item -LiteralPath $winFspMsi).Length -eq 0) {
    & curl.exe -L --fail --retry 3 --retry-delay 2 --output $winFspMsi "https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi"
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo descargar WinFsp."
    }
}

$webView2Installer = Join-Path $Prereqs "MicrosoftEdgeWebView2RuntimeInstallerX64.exe"
if (-not (Test-Path -LiteralPath $webView2Installer) -or (Get-Item -LiteralPath $webView2Installer).Length -eq 0) {
    $tempDownload = "$webView2Installer.download"
    Remove-Item -LiteralPath $tempDownload -Force -ErrorAction SilentlyContinue
    & curl.exe -L --fail --retry 3 --retry-delay 2 --output $tempDownload "https://go.microsoft.com/fwlink/?linkid=2124701"
    if ($LASTEXITCODE -ne 0) {
        throw "No se pudo descargar WebView2 Runtime."
    }

    Move-Item -LiteralPath $tempDownload -Destination $webView2Installer -Force
}

Remove-Item -LiteralPath $Assets -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Assets | Out-Null

Remove-Item -LiteralPath $UninstallPublishDir -Recurse -Force -ErrorAction SilentlyContinue
& $Dotnet publish $UninstallProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false -o $UninstallPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish del desinstalador fallo."
}

$uninstallerExe = Join-Path $UninstallPublishDir "O2CloudDrive.Uninstall.exe"
if (-not (Test-Path -LiteralPath $uninstallerExe)) {
    throw "No se encontro el ejecutable publicado del desinstalador."
}

Remove-Item -LiteralPath $PayloadSource -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $PayloadSource | Out-Null
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $PayloadSource -Recurse -Force
Remove-Item -LiteralPath (Join-Path $PayloadSource "LEEME.txt") -Force -ErrorAction SilentlyContinue

Remove-Item -LiteralPath $PayloadZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $PayloadSource "*") -DestinationPath $PayloadZip -CompressionLevel Optimal

Copy-Item -LiteralPath $winFspMsi -Destination (Join-Path $Assets "winfsp-2.1.25156.msi") -Force
Copy-Item -LiteralPath $webView2Installer -Destination (Join-Path $Assets "MicrosoftEdgeWebView2RuntimeInstallerX64.exe") -Force
Copy-Item -LiteralPath $uninstallerExe -Destination (Join-Path $Assets "O2CloudDrive.Uninstall.exe") -Force

Remove-Item -LiteralPath $SetupPublishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $SetupExe -Force -ErrorAction SilentlyContinue

& $Dotnet publish $SetupProject -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false -o $SetupPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish del instalador fallo."
}

$publishedSetupExe = Join-Path $SetupPublishDir "O2CloudDrive-0.5-beta-Setup.exe"
if (-not (Test-Path -LiteralPath $publishedSetupExe)) {
    throw "No se encontro el ejecutable publicado del instalador."
}

Remove-Item -LiteralPath $SetupExe -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $publishedSetupExe -Destination $SetupExe -Force
Remove-Item -LiteralPath $publishedSetupExe -Force -ErrorAction SilentlyContinue

$hash = Get-FileHash -LiteralPath $SetupExe -Algorithm SHA256
Set-Content -LiteralPath $HashFile -Value "$($hash.Hash)  $(Split-Path -Leaf $SetupExe)" -Encoding ASCII

Write-Host "Instalador generado: $SetupExe"
Write-Host "SHA256: $($hash.Hash)"
