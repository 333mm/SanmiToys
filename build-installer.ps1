# ==========================================================
# SanmiToys - Dual Installer & Package Build Script
# Supports: Win32 (Velopack) and Microsoft Store (MSIX)
# ==========================================================
param (
    [string]$Version = "",
    [ValidateSet("All", "Win32", "Msix")]
    [string]$Target = "All",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

# 1. バージョン取得（指定がない場合は Directory.Build.props から取得）
if ([string]::IsNullOrWhiteSpace($Version)) {
    $propsPath = Join-Path $PSScriptRoot "Directory.Build.props"
    if (Test-Path $propsPath) {
        [xml]$propsXml = Get-Content $propsPath
        $Version = $propsXml.Project.PropertyGroup.Version
    }
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = "1.0.0"
    }
}

# MSIX 用の 4 桁数値バージョン生成 (例: 1.0.0 -> 1.0.0.0, 1.0.0-beta.1 -> 1.0.0.0)
$cleanVer = $Version.Split('-')[0]
$verParts = $cleanVer.Split('.')
while ($verParts.Count -lt 4) {
    $verParts += "0"
}
$msixVersion = ($verParts[0..3] -join ".")

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building SanmiToys (v$Version / MSIX: $msixVersion)" -ForegroundColor Cyan
Write-Host " Target: $Target" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 2. 出力先ディレクトリの準備
$publishDir = "./publish/SanmiToys"
$releasesDir = "./Releases"
$win32ReleasesDir = "./Releases/Win32"
$msixReleasesDir = "./Releases/MSIX"

if (!(Test-Path $releasesDir)) { New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null }
if (!(Test-Path $win32ReleasesDir)) { New-Item -ItemType Directory -Path $win32ReleasesDir -Force | Out-Null }
if (!(Test-Path $msixReleasesDir)) { New-Item -ItemType Directory -Path $msixReleasesDir -Force | Out-Null }

if ($Clean) {
    Write-Host "Cleaning releases directories for a fresh build..." -ForegroundColor Gray
    Get-ChildItem -Path $win32ReleasesDir | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path $msixReleasesDir | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# 3. dotnet publish（Release ビルド）
Write-Host "`n[Step 1/3] Publishing .NET 8 Release binaries..." -ForegroundColor Yellow
dotnet publish src/SanmiToys.Host/SanmiToys.Host.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -p:AssemblyVersion=$msixVersion `
    -p:FileVersion=$msixVersion `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
    exit 1
}

# 4. Win32 (Velopack) パッケージ生成
if ($Target -in @("All", "Win32")) {
    Write-Host "`n[Step 2/3] Packaging Win32 with Velopack (vpk)..." -ForegroundColor Yellow
    vpk pack `
        -u SanmiToys `
        -v $Version `
        -p $publishDir `
        -e SanmiToys.Host.exe `
        -i src/SanmiToys.Host/Assets/app.ico `
        -o $win32ReleasesDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "vpk pack failed!"
        exit 1
    }
    # ルート Releases にも最新の Setup やリリース成果物をコピー
    Copy-Item "$win32ReleasesDir\*" -Destination "$releasesDir\" -Force -ErrorAction SilentlyContinue
    # 旧バージョンの .nupkg がルート Releases に残っている場合は整理
    Get-ChildItem -Path "$releasesDir" -Filter "SanmiToys-*-full.nupkg" |
        Where-Object { $_.Name -notmatch [regex]::Escape($Version) } |
        Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Host "  -> Win32 installer generated: $releasesDir\SanmiToys-win-Setup.exe" -ForegroundColor Green
}

# 5. MSIX パッケージ生成
if ($Target -in @("All", "Msix")) {
    Write-Host "`n[Step 3/3] Packaging MSIX for Microsoft Store..." -ForegroundColor Yellow

    # makeappx.exe の検出
    $sdkMakeAppx = Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\bin" -Filter "makeappx.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match "x64" } |
        Select-Object -First 1 -ExpandProperty FullName

    if (!$sdkMakeAppx -or !(Test-Path $sdkMakeAppx)) {
        Write-Error "makeappx.exe not found in Windows Kits directory! Please verify Windows SDK installation."
        exit 1
    }

    # MSIX アセット生成（存在しない場合）
    $msixAssetsDir = "./packaging/msix/Assets"
    if (!(Test-Path "$msixAssetsDir/Square150x150Logo.png")) {
        & "$PSScriptRoot/packaging/msix/generate-assets.ps1"
    }

    # MSIX レイアウトディレクトリ作成
    $msixLayoutDir = "./publish/MsixLayout"
    if (Test-Path $msixLayoutDir) { Remove-Item -Recurse -Force $msixLayoutDir }
    New-Item -ItemType Directory -Path $msixLayoutDir -Force | Out-Null

    # 発行成果物をレイアウトにコピー
    Copy-Item -Path "$publishDir/*" -Destination $msixLayoutDir -Recurse -Force

    # Assets ディレクトリをコピー
    $targetAssetsDir = Join-Path $msixLayoutDir "Assets"
    if (!(Test-Path $targetAssetsDir)) { New-Item -ItemType Directory -Path $targetAssetsDir -Force | Out-Null }
    Copy-Item -Path "$msixAssetsDir/*" -Destination $targetAssetsDir -Force

    # AppxManifest.xml の Identity Version を書き換えて配置 (xml宣言やMinVersionを誤置換しないよう厳密に一致)
    $manifestTemplate = Get-Content -Path "./packaging/msix/AppxManifest.xml" -Raw
    $manifestContent = $manifestTemplate -replace '(<Identity\b[^>]*\bVersion=")[^"]*(")', "`${1}$msixVersion`$2"
    [System.IO.File]::WriteAllText((Join-Path $msixLayoutDir "AppxManifest.xml"), $manifestContent, (New-Object System.Text.UTF8Encoding($false)))


    # makeappx pack 実行
    $msixOutFile = "$msixReleasesDir\SanmiToys_${Version}_x64.msix"
    & $sdkMakeAppx pack /d $msixLayoutDir /p $msixOutFile /o

    if ($LASTEXITCODE -ne 0) {
        Write-Error "makeappx pack failed!"
        exit 1
    }

    Write-Host "  -> MSIX package generated: $msixOutFile" -ForegroundColor Green
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " BUILD SUCCESS!" -ForegroundColor Green
if ($Target -in @("All", "Win32")) {
    Write-Host " - Win32 Setup : $releasesDir\SanmiToys-win-Setup.exe" -ForegroundColor Green
}
if ($Target -in @("All", "Msix")) {
    Write-Host " - MSIX Package: $msixReleasesDir\SanmiToys_${Version}_x64.msix" -ForegroundColor Green
}
Write-Host "==========================================================" -ForegroundColor Green
