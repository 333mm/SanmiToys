# ==============================================================================
# SanmiToys - Microsoft Store 実際のアプリスクリーンショット自動取得＆スタイリングスクリプト
# ==============================================================================
# Microsoft Store ポリシー 10.1.1.3 (Inaccurate Representation) に準拠するため、
# 実際のアプリを英語ロケール・ダークテーマ・1920x1080 (16:9) で起動して各モジュール画面を
# 直接キャプチャし、ストア掲載用の方針B (洗練されたFluent実機スタイル) 画像を自動合成します。
#
# 使い方:
#   .\capture-store-screenshots.ps1
#   .\capture-store-screenshots.ps1 -Language en
#   .\capture-store-screenshots.ps1 -OutputDir ".\Releases\StoreListingAssets\Screenshots"
# ==============================================================================

param (
    [string]$AppExe    = "$PSScriptRoot\src\SanmiToys.Host\bin\Release\net8.0-windows10.0.19041.0\SanmiToys.Host.exe",
    [string]$OutputDir = "$PSScriptRoot\Releases\StoreListingAssets\Screenshots",
    [string]$Language  = "en"
)

$ErrorActionPreference = "Stop"

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " SanmiToys ストア用スクリーンショット自動取得・生成" -ForegroundColor Cyan
Write-Host " ※ Microsoft Store ポリシー 10.1.1.3 準拠 (実アプリ直接キャプチャ＆方針B)" -ForegroundColor Cyan
Write-Host "========================================================`n" -ForegroundColor Cyan

# 1. アプリ実行ファイルの確認・ビルド
if (!(Test-Path $AppExe)) {
    Write-Host "実行ファイルが見つかりません。Release ビルドを実行します..." -ForegroundColor Yellow
    dotnet build "$PSScriptRoot\src\SanmiToys.Host\SanmiToys.Host.csproj" -c Release
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ビルドに失敗しました。"
        exit 1
    }
}

$rawScreenshotsDir  = "$OutputDir\raw"
$docsScreenshotsDir = "$PSScriptRoot\docs\store-screenshots"

foreach ($dir in @($OutputDir, $rawScreenshotsDir, $docsScreenshotsDir)) {
    if (!(Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# 2. 実アプリ直接キャプチャの実行 (raw フォルダへ保存)
Write-Host "実アプリをキャプチャモード (言語: $Language, 解像度: 1920x1080) で実行します..." -ForegroundColor Yellow
$proc = Start-Process -FilePath $AppExe -ArgumentList @("--capture-screenshots", "`"$rawScreenshotsDir`"", "--lang", "$Language") -PassThru -Wait

if ($proc.ExitCode -ne 0) {
    Write-Error "スクリーンショットのキャプチャ中にエラーが発生しました (終了コード: $($proc.ExitCode))"
    exit $proc.ExitCode
}

# raw 内のエイリアス正規化 (01_Overview.png 等)
$expectedRaw = @(
    "Screenshot_01_Overview.png",
    "Screenshot_02_FluidDrag.png",
    "Screenshot_03_FocusDimmer.png",
    "Screenshot_04_SnapTrans.png",
    "Screenshot_05_SwiftVolume.png",
    "Screenshot_06_OmniGlance.png"
)

foreach ($file in $expectedRaw) {
    $rawSrc = Join-Path $rawScreenshotsDir $file
    if (Test-Path $rawSrc) {
        $shortName = $file.Replace("Screenshot_", "")
        Copy-Item -Path $rawSrc -Destination (Join-Path $rawScreenshotsDir $shortName) -Force
    }
}

# 3. 方針B スタイリッシュスクリーンショットの生成と docs への同期
Write-Host "`n方針B スタイリッシュスクリーンショットを合成中..." -ForegroundColor Cyan
& "$PSScriptRoot\generate-store-screenshots.ps1" -OutputDir $OutputDir -RawDir $rawScreenshotsDir -DocsDir $docsScreenshotsDir

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " 全 6 枚の直接キャプチャ・スタイリッシュスクリーンショットを更新しました！" -ForegroundColor Green
Write-Host "  ストア掲載用出力先: $OutputDir" -ForegroundColor Green
Write-Host "  Rawキャプチャ保存先: $rawScreenshotsDir" -ForegroundColor Green
Write-Host "  ドキュメント同期先: $docsScreenshotsDir" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "次のステップ (Microsoft Partner Center 再提出):" -ForegroundColor Yellow
Write-Host "  1. Microsoft Partner Center にサインイン"
Write-Host "  2. 対象アプリの「Store listing (ストアの掲載情報)」->「English」を開く"
Write-Host "  3. 既存の宣伝バナー画像を削除し、$OutputDir 配下の画像をアップロード"
Write-Host "  4. 「Submit to the Store」をクリックして再申請"
