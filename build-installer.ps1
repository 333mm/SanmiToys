# ==========================================================
# SanmiToys - Velopack Installer Build Script
# ==========================================================
param (
    [string]$Version = "1.0.0-beta.1"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building SanmiToys Velopack Installer (v$Version) ... " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. 出力先ディレクトリの準備
$publishDir = "./publish/SanmiToys"
$releasesDir = "./Releases"

if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
if (Test-Path $releasesDir) {
    Remove-Item -Recurse -Force $releasesDir
}

# 2. dotnet publish（リリースビルド）
Write-Host "`n[Step 1/2] Publishing .NET 8 Release binaries..." -ForegroundColor Yellow
dotnet publish src/SanmiToys.Host/SanmiToys.Host.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed!"
    exit 1
}

# 3. vpk pack（Velopack インストーラー & パッケージ生成）
Write-Host "`n[Step 2/2] Packaging with Velopack (vpk)..." -ForegroundColor Yellow
vpk pack `
    -u SanmiToys `
    -v $Version `
    -p $publishDir `
    -e SanmiToys.Host.exe `
    -i src/SanmiToys.Host/Assets/app.ico `
    -o $releasesDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "vpk pack failed!"
    exit 1
}

Write-Host "`n==========================================================" -ForegroundColor Green
Write-Host " SUCCESS! Installer generated in $releasesDir" -ForegroundColor Green
Write-Host " - Setup: $releasesDir\SanmiToys-Setup.exe" -ForegroundColor Green
Write-Host " - Packages: $releasesDir\*.nupkg" -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
