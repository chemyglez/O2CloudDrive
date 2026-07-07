param(
    [switch]$SkipPublish,
    [switch]$SkipSigning,
    [ValidateSet("Full", "Update", "UpdateNet8")]
    [string]$Variant = "Full",
    [string]$SignToolPath,
    [string]$CertificateThumbprint,
    [string]$CertificatePath,
    [string]$CertificatePasswordEnv = "O2CLOUDDRIVE_PFX_PASSWORD",
    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$PackageLabel = "0.8.3-beta"
$variantSuffix = switch ($Variant) {
    "Full" { "" }
    "Update" { "-update" }
    "UpdateNet8" { "-update-net8" }
}
$artifactName = switch ($Variant) {
    "Full" { "O2CloudDrive-$PackageLabel-Setup.exe" }
    "Update" { "O2CloudDrive-$PackageLabel-Update.exe" }
    "UpdateNet8" { "O2CloudDrive-$PackageLabel-Update-Net8.exe" }
}
$includePrerequisites = $Variant -eq "Full"
$frameworkDependent = $Variant -eq "UpdateNet8"
$selfContained = if ($frameworkDependent) { "false" } else { "true" }
$PublishDir = Join-Path $Root "dist\O2CloudDrive-$PackageLabel-win-x64$variantSuffix"
$SetupProject = Join-Path $PSScriptRoot "O2CloudDrive.Setup\O2CloudDrive.Setup.csproj"
$UninstallProject = Join-Path $PSScriptRoot "O2CloudDrive.Uninstall\O2CloudDrive.Uninstall.csproj"
$SetupPublishDir = Join-Path $Root "dist\setup-publish$variantSuffix"
$UninstallPublishDir = Join-Path $Root "dist\uninstall-publish$variantSuffix"
$SetupExe = Join-Path $Root "dist\$artifactName"
$HashFile = Join-Path $Root "dist\$([System.IO.Path]::GetFileNameWithoutExtension($artifactName)).sha256.txt"
$Project = Join-Path $Root "src\O2CloudDrive\O2CloudDrive.csproj"
$Dotnet = Join-Path $Root ".dotnet\dotnet.exe"
$Prereqs = Join-Path $PSScriptRoot "prereqs"
$Assets = Join-Path $PSScriptRoot "O2CloudDrive.Setup\Assets"
$PayloadSource = Join-Path $PSScriptRoot "payload-source"
$PayloadZip = Join-Path $Assets "payload.zip"

if (-not (Test-Path -LiteralPath $Dotnet)) {
    $Dotnet = "dotnet"
}

function Test-CodeSigningEnabled {
    return -not $SkipSigning -and (
        -not [string]::IsNullOrWhiteSpace($CertificateThumbprint) -or
        -not [string]::IsNullOrWhiteSpace($CertificatePath)
    )
}

function Resolve-SignTool {
    if (-not [string]::IsNullOrWhiteSpace($SignToolPath)) {
        if (-not (Test-Path -LiteralPath $SignToolPath)) {
            throw "No se encontro signtool.exe en $SignToolPath."
        }

        return $SignToolPath
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (-not (Test-Path -LiteralPath $kitsRoot)) {
        throw "No se encontro Windows SDK. Instala Windows SDK o indica -SignToolPath."
    }

    $tool = Get-ChildItem -LiteralPath $kitsRoot -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "\\x64\\signtool\.exe$" } |
        Sort-Object FullName -Descending |
        Select-Object -First 1

    if ($null -eq $tool) {
        throw "No se encontro signtool.exe x64. Instala Windows SDK o indica -SignToolPath."
    }

    return $tool.FullName
}

function Invoke-CodeSign {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    if (-not (Test-CodeSigningEnabled)) {
        return
    }

    if (-not (Test-Path -LiteralPath $FilePath)) {
        throw "No se puede firmar porque no existe $FilePath."
    }

    $tool = Resolve-SignTool
    $args = @("sign", "/fd", "SHA256", "/tr", $TimestampServer, "/td", "SHA256")
    if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $args += @("/sha", $CertificateThumbprint)
    }
    elseif (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        if (-not (Test-Path -LiteralPath $CertificatePath)) {
            throw "No se encontro el certificado PFX en $CertificatePath."
        }

        $args += @("/f", $CertificatePath)
        $password = [Environment]::GetEnvironmentVariable($CertificatePasswordEnv)
        if (-not [string]::IsNullOrEmpty($password)) {
            $args += @("/p", $password)
        }
    }

    $args += $FilePath
    & $tool @args
    if ($LASTEXITCODE -ne 0) {
        throw "Fallo la firma digital de $FilePath."
    }

    Write-Host "Firmado digitalmente: $FilePath"
}

if (Test-CodeSigningEnabled) {
    Write-Host "Firma digital activada."
}
else {
    Write-Host "Firma digital omitida: no se ha indicado certificado."
}

Write-Host "Variante: $Variant"
if ($includePrerequisites) {
    Write-Host "Prerrequisitos incluidos: WinFsp y WebView2."
}
else {
    Write-Host "Prerrequisitos omitidos: WinFsp y WebView2 deben estar instalados."
}

if ($frameworkDependent) {
    Write-Host "Runtime .NET omitido: requiere .NET 8 Desktop Runtime instalado."
}
else {
    Write-Host "Publicacion autocontenida: no requiere .NET instalado en el sistema."
}

if (-not $SkipPublish) {
    & $Dotnet publish $Project -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $PublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish de O2CloudDrive fallo."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $PublishDir "O2CloudDrive.exe"))) {
    throw "No se encontro la publicacion de O2CloudDrive en $PublishDir."
}
Invoke-CodeSign -FilePath (Join-Path $PublishDir "O2CloudDrive.exe")

if ($includePrerequisites) {
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
}

Remove-Item -LiteralPath $Assets -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Assets | Out-Null

Remove-Item -LiteralPath $UninstallPublishDir -Recurse -Force -ErrorAction SilentlyContinue
& $Dotnet publish $UninstallProject -c Release -r win-x64 --self-contained $selfContained -p:PublishSingleFile=true -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false -o $UninstallPublishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish del desinstalador fallo."
}

$uninstallerExe = Join-Path $UninstallPublishDir "O2CloudDrive.Uninstall.exe"
if (-not (Test-Path -LiteralPath $uninstallerExe)) {
    throw "No se encontro el ejecutable publicado del desinstalador."
}
Invoke-CodeSign -FilePath $uninstallerExe

Remove-Item -LiteralPath $PayloadSource -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $PayloadSource | Out-Null
Copy-Item -Path (Join-Path $PublishDir "*") -Destination $PayloadSource -Recurse -Force
Remove-Item -LiteralPath (Join-Path $PayloadSource "LEEME.txt") -Force -ErrorAction SilentlyContinue

Remove-Item -LiteralPath $PayloadZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $PayloadSource "*") -DestinationPath $PayloadZip -CompressionLevel Optimal

if ($includePrerequisites) {
    Copy-Item -LiteralPath $winFspMsi -Destination (Join-Path $Assets "winfsp-2.1.25156.msi") -Force
    Copy-Item -LiteralPath $webView2Installer -Destination (Join-Path $Assets "MicrosoftEdgeWebView2RuntimeInstallerX64.exe") -Force
}
Copy-Item -LiteralPath $uninstallerExe -Destination (Join-Path $Assets "O2CloudDrive.Uninstall.exe") -Force

Remove-Item -LiteralPath $SetupPublishDir -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $SetupExe -Force -ErrorAction SilentlyContinue

$setupAssemblyName = [System.IO.Path]::GetFileNameWithoutExtension($artifactName)
$setupDefines = @()
if (-not $includePrerequisites) {
    $setupDefines += "UPDATE_INSTALLER"
}

if ($frameworkDependent) {
    $setupDefines += "FRAMEWORK_DEPENDENT_INSTALLER"
}

$setupPublishArgs = @(
    "publish",
    $SetupProject,
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    $selfContained,
    "-p:PublishSingleFile=true",
    "-p:PublishReadyToRun=false",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-p:AssemblyName=$setupAssemblyName",
    "-o",
    $SetupPublishDir
)

if ($setupDefines.Count -gt 0) {
    $setupPublishArgs += "-p:DefineConstants=$($setupDefines -join '%3B')"
}

& $Dotnet @setupPublishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish del instalador fallo."
}

$publishedSetupExe = Join-Path $SetupPublishDir "$setupAssemblyName.exe"
if (-not (Test-Path -LiteralPath $publishedSetupExe)) {
    throw "No se encontro el ejecutable publicado del instalador."
}

Remove-Item -LiteralPath $SetupExe -Force -ErrorAction SilentlyContinue
Copy-Item -LiteralPath $publishedSetupExe -Destination $SetupExe -Force
Remove-Item -LiteralPath $publishedSetupExe -Force -ErrorAction SilentlyContinue
Invoke-CodeSign -FilePath $SetupExe

$hash = Get-FileHash -LiteralPath $SetupExe -Algorithm SHA256
Set-Content -LiteralPath $HashFile -Value "$($hash.Hash)  $(Split-Path -Leaf $SetupExe)" -Encoding ASCII

Write-Host "Instalador generado: $SetupExe"
Write-Host "SHA256: $($hash.Hash)"
