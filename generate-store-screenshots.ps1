# ==============================================================================
# SanmiToys - Microsoft Store 1920x1080 Stylish Screenshots (Plan B)
# ==============================================================================
# Microsoft Store ポリシー 10.1.1.3 (実アプリの直接キャプチャ必須) に準拠し、
# 実際のアプリ設定画面の 1920x1080 直接キャプチャを主役（画面占有率約80%）として埋め込み、
# Windows 11 Fluent スタイルの上品なヘッダー・シャドウ・背景グローを合成した
# 信頼性と洗練性を両立したストア掲載用スクリーンショットを自動生成します。
# ==============================================================================

param (
    [string]$OutputDir      = "$PSScriptRoot/Releases/StoreListingAssets/Screenshots",
    [string]$RawDir         = "$PSScriptRoot/Releases/StoreListingAssets/Screenshots/raw",
    [string]$DocsDir        = "$PSScriptRoot/docs/store-screenshots",
    [string]$PromotionalDir = "$PSScriptRoot/Releases/StoreListingAssets/Promotional"
)

Add-Type -AssemblyName System.Drawing

$ErrorActionPreference = "Stop"

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " SanmiToys ストア掲載用スクリーンショット生成 (方針B: スタイリッシュ実画面)" -ForegroundColor Cyan
Write-Host " ※ Microsoft Store ポリシー 10.1.1.3 準拠 (実機UI直接キャプチャ埋め込み)" -ForegroundColor Cyan
Write-Host "========================================================`n" -ForegroundColor Cyan

# ディレクトリ作成
foreach ($dir in @($OutputDir, $RawDir, $DocsDir, $PromotionalDir)) {
    if (!(Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# Raw 画像が存在しない場合は自動キャプチャを実行
$rawOverview = Join-Path $RawDir "01_Overview.png"
if (!(Test-Path $rawOverview)) {
    $rawOverview = Join-Path $RawDir "Screenshot_01_Overview.png"
}
if (!(Test-Path $rawOverview)) {
    Write-Host "Raw キャプチャ画像が見つかりません。capture-store-screenshots.ps1 を実行します..." -ForegroundColor Yellow
    & "$PSScriptRoot/capture-store-screenshots.ps1"
}

# 角丸矩形 Path 生成ヘルパー
function Create-RoundedRectanglePath {
    param (
        [float]$x, [float]$y, [float]$w, [float]$h, [float]$r
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1.0, $r * 2)
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# スタイリッシュスクリーンショット生成関数
function Generate-PlanB-Screenshot {
    param (
        [string]$RawImagePath,
        [string]$OutputPath,
        [string]$TagText,
        [string]$TitleText,
        [string]$SubTitleText,
        [System.Drawing.Color]$AccentColor
    )

    if (!(Test-Path $RawImagePath)) {
        Write-Error "入力元キャプチャ画像が存在しません: $RawImagePath"
        return
    }

    $bmp = New-Object System.Drawing.Bitmap(1920, 1080)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    # 1. Background Gradient (深いダークブルーグレーグラデーション)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(1920, 1080)),
        [System.Drawing.Color]::FromArgb(255, 14, 18, 28),
        [System.Drawing.Color]::FromArgb(255, 22, 28, 44)
    )
    $g.FillRectangle($bgBrush, 0, 0, 1920, 1080)
    $bgBrush.Dispose()

    # Ambient Radial Glow (アクセントカラーに合わせた柔らかな背景グロー)
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse(200, 50, 1520, 900)
    $pbg = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $pbg.CenterColor = [System.Drawing.Color]::FromArgb(35, $AccentColor.R, $AccentColor.G, $AccentColor.B)
    $pbg.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 14, 18, 28))
    $g.FillPath($pbg, $glowPath)
    $glowPath.Dispose()
    $pbg.Dispose()

    # 2. Header (上部ヘッダー情報)
    # ピルタグ (モジュールカテゴリ)
    $fTag = New-Object System.Drawing.Font("Segoe UI", 10.5, [System.Drawing.FontStyle]::Bold)
    $tagMeasure = $g.MeasureString($TagText, $fTag)
    $tagW = [Math]::Max(120.0, [Math]::Round($tagMeasure.Width + 28))
    $tagH = 28.0

    $tagPath = Create-RoundedRectanglePath 200 45 $tagW $tagH 14
    $tagBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, $AccentColor.R, $AccentColor.G, $AccentColor.B))
    $tagPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(140, $AccentColor.R, $AccentColor.G, $AccentColor.B), 1.2)
    $g.FillPath($tagBrush, $tagPath)
    $g.DrawPath($tagPen, $tagPath)
    $tagPath.Dispose()
    $tagBrush.Dispose()
    $tagPen.Dispose()

    $bTag = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(215, 235, 255))
    $sfTag = New-Object System.Drawing.StringFormat
    $sfTag.Alignment = [System.Drawing.StringAlignment]::Center
    $sfTag.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString($TagText, $fTag, $bTag, (New-Object System.Drawing.RectangleF(200, 45, $tagW, $tagH)), $sfTag)
    $fTag.Dispose()
    $bTag.Dispose()
    $sfTag.Dispose()

    # メインタイトル
    $fTitle = New-Object System.Drawing.Font("Segoe UI", 26, [System.Drawing.FontStyle]::Bold)
    $bTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 248, 255))
    $g.DrawString($TitleText, $fTitle, $bTitle, 200, 78)
    $fTitle.Dispose()
    $bTitle.Dispose()

    # サブタイトル
    $fSub = New-Object System.Drawing.Font("Segoe UI", 13.5)
    $bSub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(170, 185, 205))
    $g.DrawString($SubTitleText, $fSub, $bSub, 200, 126)
    $fSub.Dispose()
    $bSub.Dispose()

    # 3. Real App Window (実アプリUIキャプチャを主役として中央配置: 1520x855)
    $img = [System.Drawing.Image]::FromFile($RawImagePath)
    $winX = 200; $winY = 175; $winW = 1520; $winH = 855

    # Multi-layer Elevation Drop Shadow (Windows 11 スタイルの多層シャドウ)
    for ($i = 3; $i -ge 1; $i--) {
        $sAlpha = 20 * $i
        $sSpread = 8 * $i
        $sBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($sAlpha, 0, 0, 0))
        $sPath = Create-RoundedRectanglePath ($winX - $sSpread) ($winY - $sSpread + 12) ($winW + $sSpread * 2) ($winH + $sSpread * 2) (14 + $i * 4)
        $g.FillPath($sBrush, $sPath)
        $sBrush.Dispose()
        $sPath.Dispose()
    }

    # Clip & Draw Real Window (角丸 14px でクリーンにクリッピング描画)
    $clipPath = Create-RoundedRectanglePath $winX $winY $winW $winH 14
    $oldClip = $g.Clip
    $g.SetClip($clipPath)
    $g.DrawImage($img, $winX, $winY, $winW, $winH)
    $g.Clip = $oldClip
    $clipPath.Dispose()

    # Fluent Border (半透明ホワイト境界線)
    $winPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 255, 255, 255), 1.5)
    $borderPath = Create-RoundedRectanglePath $winX $winY $winW $winH 14
    $g.DrawPath($winPen, $borderPath)
    $winPen.Dispose()
    $borderPath.Dispose()

    # Subtle Accent Outline (アクセントカラーのアウトライン)
    $glowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, $AccentColor.R, $AccentColor.G, $AccentColor.B), 1.2)
    $gPath = Create-RoundedRectanglePath ($winX - 1) ($winY - 1) ($winW + 2) ($winH + 2) 15
    $g.DrawPath($glowPen, $gPath)
    $glowPen.Dispose()
    $gPath.Dispose()

    $img.Dispose()

    # 保存
    $bmp.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    $bmp.Dispose()

    $fileInfo = Get-Item $OutputPath
    Write-Host "  [OK] $([System.IO.Path]::GetFileName($OutputPath)) ($([math]::Round($fileInfo.Length / 1KB, 1)) KB)" -ForegroundColor Green
}

# Raw 画像解決ヘルパー
function Resolve-RawImage {
    param ([string]$Dir, [string[]]$Names)
    foreach ($name in $Names) {
        $candidate = Join-Path $Dir $name
        if (Test-Path $candidate) {
            return $candidate
        }
    }
    return $null
}

# 6 つのモジュール画面定義
$modules = @(
    @{
        RawNames = @("01_Overview.png", "01_Dashboard.png", "Screenshot_01_Overview.png")
        OutFile  = "Screenshot_01_Overview.png"
        DocsFile = "01_Overview.png"
        Tag      = "SUITE OVERVIEW"
        Title    = "All-in-One Modular Desktop Productivity Suite"
        Sub      = "Five essential desktop utilities running in a unified, lightweight host for Windows 10 & 11."
        Color    = [System.Drawing.Color]::FromArgb(0, 162, 255)
    },
    @{
        RawNames = @("02_FluidDrag.png", "Screenshot_02_FluidDrag.png")
        OutFile  = "Screenshot_02_FluidDrag.png"
        DocsFile = "02_FluidDrag.png"
        Tag      = "FLUID DRAG"
        Title    = "Effortless Window Dragging from Any Background Space"
        Sub      = "Click and drag anywhere within windows without aiming for thin title bars."
        Color    = [System.Drawing.Color]::FromArgb(6, 182, 212)
    },
    @{
        RawNames = @("03_FocusDimmer.png", "Screenshot_03_FocusDimmer.png")
        OutFile  = "Screenshot_03_FocusDimmer.png"
        DocsFile = "03_FocusDimmer.png"
        Tag      = "FOCUS DIMMER"
        Title    = "Eliminate Distractions with Ambient Screen Dimming"
        Sub      = "Automatically dims inactive background windows to keep your focus strictly on active tasks."
        Color    = [System.Drawing.Color]::FromArgb(167, 139, 250)
    },
    @{
        RawNames = @("04_SnapTrans.png", "Screenshot_04_SnapTrans.png")
        OutFile  = "Screenshot_04_SnapTrans.png"
        DocsFile = "04_SnapTrans.png"
        Tag      = "SNAP TRANS"
        Title    = "Snipping OCR, Real-Time Translation & Natural TTS"
        Sub      = "Snip or select any screen text for instant AI translation and speech playback."
        Color    = [System.Drawing.Color]::FromArgb(16, 185, 129)
    },
    @{
        RawNames = @("05_SwiftVolume.png", "Screenshot_05_SwiftVolume.png")
        OutFile  = "Screenshot_05_SwiftVolume.png"
        DocsFile = "05_SwiftVolume.png"
        Tag      = "SWIFT VOLUME"
        Title    = "Taskbar Mouse Wheel Volume & App Audio Mixer"
        Sub      = "Scroll anywhere over the taskbar to adjust volume, monitor microphones, or mix individual apps."
        Color    = [System.Drawing.Color]::FromArgb(245, 158, 11)
    },
    @{
        RawNames = @("06_OmniGlance.png", "Screenshot_06_OmniGlance.png")
        OutFile  = "Screenshot_06_OmniGlance.png"
        DocsFile = "06_OmniGlance.png"
        Tag      = "OMNI GLANCE"
        Title    = "Smart Status Island for Battery, Hardware & Time"
        Sub      = "Docked or floating status overlay monitoring wireless device battery, CPU/RAM load, and calendar."
        Color    = [System.Drawing.Color]::FromArgb(236, 72, 153)
    }
)

Write-Host "方針B スタイリッシュスクリーンショットを生成中..." -ForegroundColor Cyan

foreach ($m in $modules) {
    $rawPath = Resolve-RawImage -Dir $RawDir -Names $m.RawNames
    if (!$rawPath) {
        Write-Error "Raw 画像が見つかりません ($($m.RawNames -join ', ')) in $RawDir"
        continue
    }

    $targetOut = Join-Path $OutputDir $m.OutFile
    Generate-PlanB-Screenshot `
        -RawImagePath $rawPath `
        -OutputPath $targetOut `
        -TagText $m.Tag `
        -TitleText $m.Title `
        -SubTitleText $m.Sub `
        -AccentColor $m.Color

    # docs/store-screenshots/ への同期
    if ($DocsDir -and (Test-Path $DocsDir)) {
        Copy-Item -Path $targetOut -Destination (Join-Path $DocsDir $m.DocsFile) -Force
    }

    # Promotional フォルダへの同期
    if ($PromotionalDir -and (Test-Path $PromotionalDir)) {
        Copy-Item -Path $targetOut -Destination (Join-Path $PromotionalDir $m.OutFile) -Force
    }
}

# 互換用 Dashboard エイリアス (Screenshot_01_Dashboard.png & 01_Dashboard.png)
$ovSrc = Join-Path $OutputDir "Screenshot_01_Overview.png"
if (Test-Path $ovSrc) {
    Copy-Item -Path $ovSrc -Destination (Join-Path $OutputDir "Screenshot_01_Dashboard.png") -Force
    if (Test-Path $DocsDir) {
        Copy-Item -Path $ovSrc -Destination (Join-Path $DocsDir "01_Dashboard.png") -Force
    }
}

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " 全 6 枚のスタイリッシュスクリーンショットを生成・更新しました！" -ForegroundColor Green
Write-Host "  出力先: $OutputDir" -ForegroundColor Green
Write-Host "  ドキュメント同期先: $DocsDir" -ForegroundColor Green
Write-Host "========================================================`n" -ForegroundColor Cyan
