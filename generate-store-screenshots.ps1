# ==============================================================================
# SanmiToys - Microsoft Store 1920x1080 English Promotional Screenshots
# ==============================================================================
param (
    [string]$OutputDir = "$PSScriptRoot/Releases/StoreListingAssets/Screenshots",
    [string]$RawDir    = "$PSScriptRoot/Releases/StoreListingAssets/Screenshots/raw",
    [string]$IconPath  = "$PSScriptRoot/src/SanmiToys.Host/Assets/app.png",
    [switch]$UseRawImages
)

Add-Type -AssemblyName System.Drawing

if (!(Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

$iconImage = $null
if (Test-Path $IconPath) {
    $iconImage = [System.Drawing.Image]::FromFile((Resolve-Path $IconPath).Path)
}

function Create-RoundedRectanglePath {
    param (
        [float]$x, [float]$y, [float]$w, [float]$h, [float]$r
    )
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = [Math]::Max(1.0, $r * 2)
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $w - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $w - $diameter, $y + $h - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $h - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Fill-RoundedRectangle {
    param (
        [System.Drawing.Graphics]$g,
        [System.Drawing.Brush]$brush,
        [float]$x, [float]$y, [float]$w, [float]$h, [float]$r
    )
    $path = Create-RoundedRectanglePath $x $y $w $h $r
    $g.FillPath($brush, $path)
    $path.Dispose()
}

function Draw-RoundedRectangle {
    param (
        [System.Drawing.Graphics]$g,
        [System.Drawing.Pen]$pen,
        [float]$x, [float]$y, [float]$w, [float]$h, [float]$r
    )
    $path = Create-RoundedRectanglePath $x $y $w $h $r
    $g.DrawPath($pen, $path)
    $path.Dispose()
}

function Draw-BaseCanvas {
    param (
        [System.Drawing.Graphics]$g,
        [int]$width,
        [int]$height,
        [string]$moduleTag,
        [string]$mainTitle,
        [string]$subTitle,
        [System.Drawing.Color]$accentColor
    )
    # Deep Dark Gradient Background
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($width, $height)),
        [System.Drawing.Color]::FromArgb(255, 14, 18, 28),
        [System.Drawing.Color]::FromArgb(255, 24, 32, 50)
    )
    $g.FillRectangle($bgBrush, 0, 0, $width, $height)
    $bgBrush.Dispose()

    # Ambient Glow Effect
    $glowPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glowPath.AddEllipse(200, -100, 1500, 600)
    $pbg = New-Object System.Drawing.Drawing2D.PathGradientBrush($glowPath)
    $pbg.CenterColor = [System.Drawing.Color]::FromArgb(45, $accentColor.R, $accentColor.G, $accentColor.B)
    $pbg.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 14, 18, 28))
    $g.FillPath($pbg, $glowPath)
    $glowPath.Dispose()
    $pbg.Dispose()

    # Header App Icon & Title
    if ($iconImage) {
        $g.DrawImage($iconImage, 100, 60, 56, 56)
    }
    $fontHeader = New-Object System.Drawing.Font("Segoe UI", 20, [System.Drawing.FontStyle]::Bold)
    $brushHeader = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 240, 245))
    $g.DrawString("SanmiToys", $fontHeader, $brushHeader, 170, 72)
    $fontHeader.Dispose()
    $brushHeader.Dispose()

    # Module Pill Tag
    if ($moduleTag) {
        $tagPath = Create-RoundedRectanglePath 320 74 190 32 16
        $tagBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, $accentColor.R, $accentColor.G, $accentColor.B))
        $tagPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(120, $accentColor.R, $accentColor.G, $accentColor.B), 1.5)
        $g.FillPath($tagBrush, $tagPath)
        $g.DrawPath($tagPen, $tagPath)
        
        $fontTag = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
        $brushTag = New-Object System.Drawing.SolidBrush($accentColor)
        $sf = New-Object System.Drawing.StringFormat
        $sf.Alignment = [System.Drawing.StringAlignment]::Center
        $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
        $g.DrawString($moduleTag, $fontTag, $brushTag, (New-Object System.Drawing.RectangleF(320, 74, 190, 32)), $sf)
        
        $tagPath.Dispose(); $tagBrush.Dispose(); $tagPen.Dispose(); $fontTag.Dispose(); $brushTag.Dispose(); $sf.Dispose()
    }

    # Main Title & Subtitle
    $fontTitle = New-Object System.Drawing.Font("Segoe UI", 36, [System.Drawing.FontStyle]::Bold)
    $brushTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 255, 255))
    $g.DrawString($mainTitle, $fontTitle, $brushTitle, 100, 130)
    $fontTitle.Dispose()
    $brushTitle.Dispose()

    $fontSub = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Regular)
    $brushSub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 195, 215))
    $g.DrawString($subTitle, $fontSub, $brushSub, 100, 195)
    $fontSub.Dispose()
    $brushSub.Dispose()
}

# ------------------------------------------------------------------------------
# Screenshot 1: Overview
# ------------------------------------------------------------------------------
function Generate-Screenshot-Overview {
    $bmp = New-Object System.Drawing.Bitmap(1920, 1080)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $accent = [System.Drawing.Color]::FromArgb(0, 162, 255)
    Draw-BaseCanvas $g 1920 1080 "SUITE OVERVIEW" "All-in-One Modular Desktop Productivity Suite" "Supercharge your productivity, deep focus, and multitasking across Windows 10 & 11." $accent

    # Left Capabilities Summary Card
    $fW = 560; $fH = 650; $fX = 100; $fY = 275
    $fBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(215, 24, 32, 50))
    Fill-RoundedRectangle $g $fBrush $fX $fY $fW $fH 22
    $fBrush.Dispose()
    $fPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 0, 162, 255), 1.5)
    Draw-RoundedRectangle $g $fPen $fX $fY $fW $fH 22
    $fPen.Dispose()

    $fTitle = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $bTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("Unified Productivity Suite", $fTitle, $bTitle, ($fX + 36), ($fY + 32))
    $fTitle.Dispose(); $bTitle.Dispose()

    $fSub = New-Object System.Drawing.Font("Segoe UI", 12)
    $bSub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 200, 225))
    $g.DrawString("5 essential desktop utilities running in a unified lightweight host:", $fSub, $bSub, ($fX + 36), ($fY + 72))
    $fSub.Dispose(); $bSub.Dispose()

    $modules = @(
        @{ Name="FluidDrag";   Icon="◆"; Color=[System.Drawing.Color]::FromArgb(0, 195, 255); Desc="Move any window seamlessly by clicking empty areas—no title bar needed." },
        @{ Name="FocusDimmer"; Icon="★"; Color=[System.Drawing.Color]::FromArgb(150, 120, 255); Desc="Ambient inactive background dimming to maximize immersion and focus." },
        @{ Name="SnapTrans";   Icon="●"; Color=[System.Drawing.Color]::FromArgb(0, 220, 170); Desc="Snipping OCR text extraction with real-time AI translation & natural TTS." },
        @{ Name="SwiftVolume"; Icon="▲"; Color=[System.Drawing.Color]::FromArgb(255, 175, 20); Desc="Taskbar mouse wheel volume HUD and independent per-app audio mixer." },
        @{ Name="OmniGlance";  Icon="■"; Color=[System.Drawing.Color]::FromArgb(255, 100, 150); Desc="Smart status island for wireless battery levels, CPU/RAM meters & clock." }
    )

    $itemY = $fY + 112
    $fMTitle = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
    $fMDesc  = New-Object System.Drawing.Font("Segoe UI", 11.5)
    $bMDesc  = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 225, 240))

    foreach ($m in $modules) {
        $bMIcon = New-Object System.Drawing.SolidBrush($m.Color)
        $g.DrawString($m.Icon + "  " + $m.Name, $fMTitle, $bMIcon, ($fX + 36), $itemY)
        $bMIcon.Dispose()

        $rectDesc = New-Object System.Drawing.RectangleF(($fX + 38), ($itemY + 24), ($fW - 74), 48)
        $g.DrawString($m.Desc, $fMDesc, $bMDesc, $rectDesc)
        $itemY += 76
    }
    $fMTitle.Dispose(); $fMDesc.Dispose(); $bMDesc.Dispose()

    # Left Card Bottom Pill
    $pillY = $fY + $fH - 58
    $pillPath = Create-RoundedRectanglePath ($fX + 36) $pillY ($fW - 72) 34 10
    $pillBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40, 0, 162, 255))
    $pillPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(90, 0, 162, 255), 1)
    $g.FillPath($pillBrush, $pillPath)
    $g.DrawPath($pillPen, $pillPath)
    $pillPath.Dispose(); $pillBrush.Dispose(); $pillPen.Dispose()

    $fPill = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
    $bPill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(160, 215, 255))
    $sfPill = New-Object System.Drawing.StringFormat
    $sfPill.Alignment = [System.Drawing.StringAlignment]::Center
    $sfPill.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString("100% Local-First Architecture  •  Zero Background Overhead", $fPill, $bPill, (New-Object System.Drawing.RectangleF(($fX + 36), $pillY, ($fW - 72), 34)), $sfPill)
    $fPill.Dispose(); $bPill.Dispose(); $sfPill.Dispose()

    # Right: Real Application Window Screenshot
    $rawDashboard = Join-Path $RawDir "01_Dashboard.png"
    if (!(Test-Path $rawDashboard)) {
        $rawDashboard = Join-Path $RawDir "01_Overview.png"
    }

    if (Test-Path $rawDashboard) {
        $realAppImg = [System.Drawing.Image]::FromFile((Resolve-Path $rawDashboard).Path)

        $winW = 1110
        $winH = [int]($winW * ($realAppImg.Height / [double]$realAppImg.Width))
        $winX = 705
        $winY = 275 + [int]((650 - $winH) / 2)

        # Multi-layer Ambient Drop Shadow
        $s1Brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(50, 0, 0, 0))
        Fill-RoundedRectangle $g $s1Brush ($winX + 16) ($winY + 22) ($winW) ($winH) 24
        $s1Brush.Dispose()

        $s2Brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(90, 0, 0, 0))
        Fill-RoundedRectangle $g $s2Brush ($winX + 8) ($winY + 12) ($winW) ($winH) 20
        $s2Brush.Dispose()

        # Draw Real App Window
        $g.DrawImage($realAppImg, $winX, $winY, $winW, $winH)

        # Fluent Window Border
        $winPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(85, 255, 255, 255), 1.5)
        Draw-RoundedRectangle $g $winPen $winX $winY $winW $winH 12
        $winPen.Dispose()

        # Subtle Cyan Accent Outline
        $glowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 0, 162, 255), 1.5)
        Draw-RoundedRectangle $g $glowPen ($winX - 2) ($winY - 2) ($winW + 4) ($winH + 4) 14
        $glowPen.Dispose()

        $realAppImg.Dispose()
    }

    # Bottom Highlights
    $features = @("Native Windows 11 Fluent UI", "Ultra-Lightweight & Fast", "5 Languages Localized", "100% Privacy & Local-First", "High-Performance Background Daemon")
    $fBadge = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
    $bBadge = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 200, 255))
    $badgeText = $features -join "     |     "
    $rectB = New-Object System.Drawing.RectangleF(100, 960, 1720, 40)
    $sfB = New-Object System.Drawing.StringFormat
    $sfB.Alignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString($badgeText, $fBadge, $bBadge, $rectB, $sfB)
    $fBadge.Dispose(); $bBadge.Dispose(); $sfB.Dispose()

    $outPath = Join-Path $OutputDir "Screenshot_01_Overview.png"
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Generated: $outPath"
}

# ------------------------------------------------------------------------------
# Screenshot 2: FluidDrag
# ------------------------------------------------------------------------------
function Generate-Screenshot-FluidDrag {
    $bmp = New-Object System.Drawing.Bitmap(1920, 1080)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $accent = [System.Drawing.Color]::FromArgb(0, 180, 255)
    Draw-BaseCanvas $g 1920 1080 "FLUID DRAG" "Move Windows Freely from Any Background Space" "Never struggle to aim for thin title bars again. Effortlessly organize your desktop workspace." $accent

    $fW = 600; $fH = 640; $fX = 100; $fY = 280
    $fBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 24, 32, 50))
    Fill-RoundedRectangle $g $fBrush $fX $fY $fW $fH 24
    $fBrush.Dispose()
    $fPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 70, 120, 180), 1.5)
    Draw-RoundedRectangle $g $fPen $fX $fY $fW $fH 24
    $fPen.Dispose()

    $fTitle = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $bTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("Core Capabilities", $fTitle, $bTitle, ($fX + 40), ($fY + 40))
    $fTitle.Dispose(); $bTitle.Dispose()

    $points = @(
        "■ Intuitive Background Dragging`n   Click and drag any empty space within windows for effortless positioning.",
        "■ Smart Fullscreen & Maximize Detection`n   Automatically suppresses drag logic during full-screen games and media playback.",
        "■ Flexible Exclusion Management`n   Whitelist or blacklist specific applications and titles with single-click rules.",
        "■ Ultra-Low Latency Win32 Hooks`n   High-precision window manipulation delivering smooth, lag-free 60+ FPS dragging."
    )
    $fYOffset = $fY + 110
    $fPoint = New-Object System.Drawing.Font("Segoe UI", 13)
    $bPoint = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 230, 245))
    foreach ($pt in $points) {
        $g.DrawString($pt, $fPoint, $bPoint, (New-Object System.Drawing.RectangleF(($fX + 40), $fYOffset, ($fW - 80), 100)))
        $fYOffset += 115
    }
    $fPoint.Dispose(); $bPoint.Dispose()

    # Right Visual Mock
    $mW = 1040; $mH = 640; $mX = 760; $mY = 280
    $mBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230, 20, 26, 42))
    Fill-RoundedRectangle $g $mBrush $mX $mY $mW $mH 24
    $mBrush.Dispose()
    $mPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 0, 180, 255), 2)
    Draw-RoundedRectangle $g $mPen $mX $mY $mW $mH 24
    $mPen.Dispose()

    # Mock Window
    $winW = 760; $winH = 460; $winX = $mX + 140; $winY = $mY + 90
    $winBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 32, 42, 65))
    Fill-RoundedRectangle $g $winBrush $winX $winY $winW $winH 18
    $winBrush.Dispose()
    $winPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(150, 0, 180, 255), 2.5)
    Draw-RoundedRectangle $g $winPen $winX $winY $winW $winH 18
    $winPen.Dispose()

    # Title Bar
    $tbBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 42, 54, 82))
    $tbPath = Create-RoundedRectanglePath $winX $winY $winW 50 18
    $g.FillPath($tbBrush, $tbPath)
    $tbBrush.Dispose(); $tbPath.Dispose()

    $fWin = New-Object System.Drawing.Font("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)
    $bWin = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(200, 210, 230))
    $g.DrawString("Project Workspace - SanmiToys", $fWin, $bWin, ($winX + 24), ($winY + 15))
    $fWin.Dispose(); $bWin.Dispose()

    # Window Action Buttons
    $btnColors = @([System.Drawing.Color]::FromArgb(100, 200, 100), [System.Drawing.Color]::FromArgb(240, 200, 80), [System.Drawing.Color]::FromArgb(240, 90, 90))
    for ($b = 0; $b -lt 3; $b++) {
        $btnBrush = New-Object System.Drawing.SolidBrush($btnColors[$b])
        $g.FillEllipse($btnBrush, ($winX + $winW - 100 + ($b * 26)), ($winY + 18), 14, 14)
        $btnBrush.Dispose()
    }

    # Drag Area Highlight
    $dragAreaBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(35, 0, 180, 255))
    Fill-RoundedRectangle $g $dragAreaBrush ($winX + 40) ($winY + 80) ($winW - 80) ($winH - 120) 14
    $dragAreaBrush.Dispose()

    $dragPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(140, 0, 180, 255), 2)
    $dragPen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    Draw-RoundedRectangle $g $dragPen ($winX + 40) ($winY + 80) ($winW - 80) ($winH - 120) 14
    $dragPen.Dispose()

    $fDragNotice = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $bDragNotice = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 215, 255))
    $sfCenter = New-Object System.Drawing.StringFormat
    $sfCenter.Alignment = [System.Drawing.StringAlignment]::Center
    $sfCenter.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString("[ Click & Drag Any Empty Space to Move ]", $fDragNotice, $bDragNotice, (New-Object System.Drawing.RectangleF(($winX + 40), ($winY + 150), ($winW - 80), 80)), $sfCenter)
    
    $fDragSub = New-Object System.Drawing.Font("Segoe UI", 13)
    $bDragSub = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 210, 240))
    $g.DrawString("Keep your workflow fast and natural without reaching for small window borders.", $fDragSub, $bDragSub, (New-Object System.Drawing.RectangleF(($winX + 40), ($winY + 230), ($winW - 80), 50)), $sfCenter)
    $fDragNotice.Dispose(); $bDragNotice.Dispose(); $fDragSub.Dispose(); $bDragSub.Dispose(); $sfCenter.Dispose()

    $outPath = Join-Path $OutputDir "Screenshot_02_FluidDrag.png"
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Generated: $outPath"
}

# ------------------------------------------------------------------------------
# Screenshot 3: FocusDimmer
# ------------------------------------------------------------------------------
function Generate-Screenshot-FocusDimmer {
    $bmp = New-Object System.Drawing.Bitmap(1920, 1080)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $accent = [System.Drawing.Color]::FromArgb(140, 110, 255)
    Draw-BaseCanvas $g 1920 1080 "FOCUS DIMMER" "Eliminate Distractions with Ambient Screen Dimming" "Seamlessly fade out inactive background windows and multi-monitor clutter." $accent

    $fW = 600; $fH = 640; $fX = 100; $fY = 280
    $fBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 24, 32, 50))
    Fill-RoundedRectangle $g $fBrush $fX $fY $fW $fH 24
    $fBrush.Dispose()
    $fPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 140, 110, 255), 1.5)
    Draw-RoundedRectangle $g $fPen $fX $fY $fW $fH 24
    $fPen.Dispose()

    $fTitle = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $bTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("Deep Focus Experience", $fTitle, $bTitle, ($fX + 40), ($fY + 40))
    $fTitle.Dispose(); $bTitle.Dispose()

    $points = @(
        "■ Automatic Inactive Dimming`n   Effortlessly dims background windows to eliminate visual noise and blinking ads.",
        "■ Multi-Monitor Architecture`n   Synchronized dimming or per-display configurations tailored to your setup.",
        "■ Modern Palette Customization`n   Choose pure obsidian dark, midnight navy, warm amber, or custom tints.",
        "■ Interactive Window Inspector`n   Target any running application and exclude it from dimming with one click."
    )
    $fYOffset = $fY + 110
    $fPoint = New-Object System.Drawing.Font("Segoe UI", 13)
    $bPoint = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 230, 245))
    foreach ($pt in $points) {
        $g.DrawString($pt, $fPoint, $bPoint, (New-Object System.Drawing.RectangleF(($fX + 40), $fYOffset, ($fW - 80), 100)))
        $fYOffset += 115
    }
    $fPoint.Dispose(); $bPoint.Dispose()

    # Right Visual Mock
    $mW = 1040; $mH = 640; $mX = 760; $mY = 280
    $mBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 16, 20, 32))
    Fill-RoundedRectangle $g $mBrush $mX $mY $mW $mH 24
    $mBrush.Dispose()
    $mPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 140, 110, 255), 2)
    Draw-RoundedRectangle $g $mPen $mX $mY $mW $mH 24
    $mPen.Dispose()

    # Background Dimmed Window
    $dimW = 560; $dimH = 380; $dimX = $mX + 60; $dimY = $mY + 60
    $dimBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(140, 15, 20, 30))
    Fill-RoundedRectangle $g $dimBrush $dimX $dimY $dimW $dimH 16
    $dimBrush.Dispose()
    $dimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(60, 255, 255, 255), 1)
    Draw-RoundedRectangle $g $dimPen $dimX $dimY $dimW $dimH 16
    $dimPen.Dispose()
    
    $fDimText = New-Object System.Drawing.Font("Segoe UI", 13)
    $bDimText = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(100, 130, 160))
    $g.DrawString("Inactive Background Window (Auto-Dimmed)", $fDimText, $bDimText, ($dimX + 30), ($dimY + 30))
    $fDimText.Dispose(); $bDimText.Dispose()

    # Foreground Active Window
    $actW = 600; $actH = 430; $actX = $mX + 380; $actY = $mY + 150
    $actBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 34, 45, 72))
    Fill-RoundedRectangle $g $actBrush $actX $actY $actW $actH 18
    $actBrush.Dispose()
    $actPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(220, 140, 110, 255), 3)
    Draw-RoundedRectangle $g $actPen $actX $actY $actW $actH 18
    $actPen.Dispose()

    $fActTitle = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
    $bActTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("◆ Active Task Window (In Focus)", $fActTitle, $bActTitle, ($actX + 35), ($actY + 35))
    $fActTitle.Dispose(); $bActTitle.Dispose()

    $fActDesc = New-Object System.Drawing.Font("Segoe UI", 14)
    $bActDesc = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 235, 255))
    $rectAct = New-Object System.Drawing.RectangleF(($actX + 35), ($actY + 95), ($actW - 70), 200)
    $g.DrawString("Zero distraction, pure immersion.`n`nSurrounding windows and secondary monitors are smoothly dimmed to a comfortable tone.`n`nSignificantly reduces eye strain while helping you stay focused on your primary task.", $fActDesc, $bActDesc, $rectAct)
    $fActDesc.Dispose(); $bActDesc.Dispose()

    $outPath = Join-Path $OutputDir "Screenshot_03_FocusDimmer.png"
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Generated: $outPath"
}

# ------------------------------------------------------------------------------
# Screenshot 4: SnapTrans
# ------------------------------------------------------------------------------
function Generate-Screenshot-SnapTrans {
    $bmp = New-Object System.Drawing.Bitmap(1920, 1080)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $accent = [System.Drawing.Color]::FromArgb(0, 210, 160)
    Draw-BaseCanvas $g 1920 1080 "SNAP TRANS" "Snipping OCR Recognition & Real-Time AI Translation" "Instant area capture, multi-engine translation (DeepL / GPT-4 / Gemini), and natural TTS." $accent

    $fW = 600; $fH = 640; $fX = 100; $fY = 280
    $fBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 24, 32, 50))
    Fill-RoundedRectangle $g $fBrush $fX $fY $fW $fH 24
    $fBrush.Dispose()
    $fPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 0, 210, 160), 1.5)
    Draw-RoundedRectangle $g $fPen $fX $fY $fW $fH 24
    $fPen.Dispose()

    $fTitle = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $bTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("Intelligent Translation", $fTitle, $bTitle, ($fX + 40), ($fY + 40))
    $fTitle.Dispose(); $bTitle.Dispose()

    $points = @(
        "■ Snipping Precision OCR`n   Extract text instantly from non-selectable images, PDFs, videos, and games.",
        "■ Multi-Engine AI Translation`n   Switch seamlessly between DeepL, OpenAI GPT, Google Translate, and Gemini.",
        "■ Authentic Text-to-Speech (TTS)`n   Listen to native pronunciations with smooth, high-fidelity voice synthesis.",
        "■ Automatic Clipboard Sync`n   Both extracted OCR source and translated results copy to clipboard immediately."
    )
    $fYOffset = $fY + 110
    $fPoint = New-Object System.Drawing.Font("Segoe UI", 13)
    $bPoint = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 230, 245))
    foreach ($pt in $points) {
        $g.DrawString($pt, $fPoint, $bPoint, (New-Object System.Drawing.RectangleF(($fX + 40), $fYOffset, ($fW - 80), 100)))
        $fYOffset += 115
    }
    $fPoint.Dispose(); $bPoint.Dispose()

    # Right Visual Mock
    $mW = 1040; $mH = 640; $mX = 760; $mY = 280
    $mBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230, 20, 26, 42))
    Fill-RoundedRectangle $g $mBrush $mX $mY $mW $mH 24
    $mBrush.Dispose()
    $mPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 0, 210, 160), 2)
    Draw-RoundedRectangle $g $mPen $mX $mY $mW $mH 24
    $mPen.Dispose()

    # Snipping Area Mock
    $snipW = 540; $snipH = 200; $snipX = $mX + 70; $snipY = $mY + 60
    $snipPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(240, 0, 210, 160), 2)
    $snipPen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    Draw-RoundedRectangle $g $snipPen $snipX $snipY $snipW $snipH 14
    $snipPen.Dispose()

    $fSnipTag = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
    $bSnipTag = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 210, 160))
    $g.DrawString("[ Snipping Capture Area ]", $fSnipTag, $bSnipTag, ($snipX + 25), ($snipY + 20))
    $fSnipTag.Dispose(); $bSnipTag.Dispose()

    $fSnip = New-Object System.Drawing.Font("Segoe UI", 15)
    $bSnip = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 245, 255))
    $g.DrawString("Boost your daily productivity`nwith modern, next-generation Windows tools.", $fSnip, $bSnip, ($snipX + 25), ($snipY + 60))
    $fSnip.Dispose(); $bSnip.Dispose()

    # Translation Result Card
    $resW = 880; $resH = 280; $resX = $mX + 80; $resY = $mY + 300
    $resBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(250, 30, 42, 65))
    Fill-RoundedRectangle $g $resBrush $resX $resY $resW $resH 20
    $resBrush.Dispose()
    $resPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 0, 210, 160), 2.5)
    Draw-RoundedRectangle $g $resPen $resX $resY $resW $resH 20
    $resPen.Dispose()

    $fResHead = New-Object System.Drawing.Font("Segoe UI", 14, [System.Drawing.FontStyle]::Bold)
    $bResHead = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0, 210, 160))
    $g.DrawString("SnapTrans Real-Time Translation (EN -> Multi-Language)   [TTS Voice Enabled]", $fResHead, $bResHead, ($resX + 35), ($resY + 30))
    $fResHead.Dispose(); $bResHead.Dispose()

    $fResText = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $bResText = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("Elevate your personal productivity to the next level with powerful desktop utilities.", $fResText, $bResText, (New-Object System.Drawing.RectangleF(($resX + 35), ($resY + 85), ($resW - 70), 120)))
    $fResText.Dispose(); $bResText.Dispose()

    $fEngine = New-Object System.Drawing.Font("Segoe UI", 12)
    $bEngine = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(150, 190, 220))
    $g.DrawString("Engines: DeepL / OpenAI GPT-4 / Google Translate / Gemini  |  Auto-Copied to Clipboard", $fEngine, $bEngine, ($resX + 35), ($resY + 225))
    $fEngine.Dispose(); $bEngine.Dispose()

    $outPath = Join-Path $OutputDir "Screenshot_04_SnapTrans.png"
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Generated: $outPath"
}

# ------------------------------------------------------------------------------
# Screenshot 5: SwiftVolume
# ------------------------------------------------------------------------------
function Generate-Screenshot-SwiftVolume {
    $bmp = New-Object System.Drawing.Bitmap(1920, 1080)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $accent = [System.Drawing.Color]::FromArgb(255, 165, 0)
    Draw-BaseCanvas $g 1920 1080 "SWIFT VOLUME" "Fluid Mouse Wheel Volume HUD & App Mixer" "Independent taskbar tray, smooth on-screen HUD, and granular audio control." $accent

    $fW = 600; $fH = 640; $fX = 100; $fY = 280
    $fBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 24, 32, 50))
    Fill-RoundedRectangle $g $fBrush $fX $fY $fW $fH 24
    $fBrush.Dispose()
    $fPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 255, 165, 0), 1.5)
    Draw-RoundedRectangle $g $fPen $fX $fY $fW $fH 24
    $fPen.Dispose()

    $fTitle = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $bTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("Refined Audio Control", $fTitle, $bTitle, ($fX + 40), ($fY + 40))
    $fTitle.Dispose(); $bTitle.Dispose()

    $points = @(
        "■ Wheel Scroll Anywhere`n   Fine-tune master volume simply by scrolling over the taskbar or screen edges.",
        "■ Clean Fluent HUD Overlay`n   Modern, non-intrusive volume percentage indicator appears and fades smoothly.",
        "■ Granular Per-App Volume Mixer`n   Independently control browser, voice chat, music, and gaming volumes.",
        "■ One-Click Audio Output Switching`n   Toggle instantly between headphones and speakers from the system tray."
    )
    $fYOffset = $fY + 110
    $fPoint = New-Object System.Drawing.Font("Segoe UI", 13)
    $bPoint = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 230, 245))
    foreach ($pt in $points) {
        $g.DrawString($pt, $fPoint, $bPoint, (New-Object System.Drawing.RectangleF(($fX + 40), $fYOffset, ($fW - 80), 100)))
        $fYOffset += 115
    }
    $fPoint.Dispose(); $bPoint.Dispose()

    # Right Visual Mock
    $mW = 1040; $mH = 640; $mX = 760; $mY = 280
    $mBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230, 20, 26, 42))
    Fill-RoundedRectangle $g $mBrush $mX $mY $mW $mH 24
    $mBrush.Dispose()
    $mPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 255, 165, 0), 2)
    Draw-RoundedRectangle $g $mPen $mX $mY $mW $mH 24
    $mPen.Dispose()

    # Volume HUD Mock
    $hudW = 380; $hudH = 96; $hudX = $mX + 80; $hudY = $mY + 120
    $hudBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240, 28, 36, 56))
    Fill-RoundedRectangle $g $hudBrush $hudX $hudY $hudW $hudH 22
    $hudBrush.Dispose()
    $hudPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(200, 255, 165, 0), 2.5)
    Draw-RoundedRectangle $g $hudPen $hudX $hudY $hudW $hudH 22
    $hudPen.Dispose()

    $fHud = New-Object System.Drawing.Font("Segoe UI", 20, [System.Drawing.FontStyle]::Bold)
    $bHud = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("VOL  72%", $fHud, $bHud, ($hudX + 30), ($hudY + 28))
    $fHud.Dispose(); $bHud.Dispose()

    $barW = 180; $barH = 12; $barX = $hudX + 170; $barY = $hudY + 42
    $barBg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(60, 255, 255, 255))
    Fill-RoundedRectangle $g $barBg $barX $barY $barW $barH 6
    $barBg.Dispose()
    $barFill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 165, 0))
    Fill-RoundedRectangle $g $barFill $barX $barY ($barW * 0.72) $barH 6
    $barFill.Dispose()

    # Volume Mixer Mock
    $mixW = 480; $mixH = 430; $mixX = $mX + 500; $mixY = $mY + 100
    $mixBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 30, 40, 64))
    Fill-RoundedRectangle $g $mixBrush $mixX $mixY $mixW $mixH 22
    $mixBrush.Dispose()
    $mixPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 255, 255, 255), 1.5)
    Draw-RoundedRectangle $g $mixPen $mixX $mixY $mixW $mixH 22
    $mixPen.Dispose()

    $fMixH = New-Object System.Drawing.Font("Segoe UI", 16, [System.Drawing.FontStyle]::Bold)
    $bMixH = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("Volume Mixer", $fMixH, $bMixH, ($mixX + 35), ($mixY + 30))
    $fMixH.Dispose(); $bMixH.Dispose()

    $apps = @(
        @{ Title="System Master (Speakers)"; Vol="72%"; Ratio=0.72 },
        @{ Title="Google Chrome"; Vol="50%"; Ratio=0.50 },
        @{ Title="Spotify Music"; Vol="85%"; Ratio=0.85 },
        @{ Title="Discord Voice Chat"; Vol="65%"; Ratio=0.65 }
    )
    $appY = $mixY + 85
    $fApp = New-Object System.Drawing.Font("Segoe UI", 13)
    $bApp = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 230, 245))
    foreach ($a in $apps) {
        $g.DrawString("$($a.Title) - $($a.Vol)", $fApp, $bApp, ($mixX + 35), $appY)
        $sBg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(50, 255, 255, 255))
        Fill-RoundedRectangle $g $sBg ($mixX + 35) ($appY + 28) 410 8 4
        $sBg.Dispose()
        $sFl = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 170, 20))
        Fill-RoundedRectangle $g $sFl ($mixX + 35) ($appY + 28) (410 * $a.Ratio) 8 4
        $sFl.Dispose()
        $appY += 75
    }
    $fApp.Dispose(); $bApp.Dispose()

    $outPath = Join-Path $OutputDir "Screenshot_05_SwiftVolume.png"
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Generated: $outPath"
}

# ------------------------------------------------------------------------------
# Screenshot 6: OmniGlance
# ------------------------------------------------------------------------------
function Generate-Screenshot-OmniGlance {
    $bmp = New-Object System.Drawing.Bitmap(1920, 1080)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit

    $accent = [System.Drawing.Color]::FromArgb(255, 90, 140)
    Draw-BaseCanvas $g 1920 1080 "OMNI GLANCE" "Wireless Battery Island & Hardware Monitor" "Real-time battery tracking for peripherals, machine load, and smart clock." $accent

    $fW = 600; $fH = 640; $fX = 100; $fY = 280
    $fBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(210, 24, 32, 50))
    Fill-RoundedRectangle $g $fBrush $fX $fY $fW $fH 24
    $fBrush.Dispose()
    $fPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(80, 255, 90, 140), 1.5)
    Draw-RoundedRectangle $g $fPen $fX $fY $fW $fH 24
    $fPen.Dispose()

    $fTitle = New-Object System.Drawing.Font("Segoe UI", 18, [System.Drawing.FontStyle]::Bold)
    $bTitle = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("Smart Status Capsule", $fTitle, $bTitle, ($fX + 40), ($fY + 40))
    $fTitle.Dispose(); $bTitle.Dispose()

    $points = @(
        "■ Multi-Brand Wireless Battery Tracking`n   Monitors Logitech Lightspeed/Unifying, Razer HyperSpeed, Bluetooth LE, and laptop batteries.",
        "■ Breathing Pulse Low-Battery Alert`n   Gently warns you before your mouse or headset dies during games or critical meetings.",
        "■ Real-Time CPU & RAM Performance`n   Crisp, low-overhead hardware monitor lets you gauge system load at a single glance.",
        "■ Fluent Expanding Capsule & Free Placement`n   Compact pill docks anywhere (top, bottom, corners) and expands smoothly on hover."
    )
    $fYOffset = $fY + 110
    $fPoint = New-Object System.Drawing.Font("Segoe UI", 13)
    $bPoint = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(220, 230, 245))
    foreach ($pt in $points) {
        $g.DrawString($pt, $fPoint, $bPoint, (New-Object System.Drawing.RectangleF(($fX + 40), $fYOffset, ($fW - 80), 100)))
        $fYOffset += 115
    }
    $fPoint.Dispose(); $bPoint.Dispose()

    # Right Visual Mock
    $mW = 1040; $mH = 640; $mX = 760; $mY = 280
    $mBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230, 20, 26, 42))
    Fill-RoundedRectangle $g $mBrush $mX $mY $mW $mH 24
    $mBrush.Dispose()
    $mPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(100, 255, 90, 140), 2)
    Draw-RoundedRectangle $g $mPen $mX $mY $mW $mH 24
    $mPen.Dispose()

    # Expanded Island Capsule
    $islW = 880; $islH = 360; $islX = $mX + 80; $islY = $mY + 140
    $islBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(245, 26, 34, 52))
    Fill-RoundedRectangle $g $islBrush $islX $islY $islW $islH 28
    $islBrush.Dispose()
    $islPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(180, 255, 90, 140), 2.5)
    Draw-RoundedRectangle $g $islPen $islX $islY $islW $islH 28
    $islPen.Dispose()

    # Clock & Date
    $fTime = New-Object System.Drawing.Font("Segoe UI", 38, [System.Drawing.FontStyle]::Bold)
    $bTime = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.DrawString("12:45", $fTime, $bTime, ($islX + 45), ($islY + 45))
    $fTime.Dispose(); $bTime.Dispose()

    $fDate = New-Object System.Drawing.Font("Segoe UI", 15)
    $bDate = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(160, 180, 210))
    $g.DrawString("Thursday, September 10", $fDate, $bDate, ($islX + 48), ($islY + 125))
    $fDate.Dispose(); $bDate.Dispose()

    # Battery Items
    $items = @(
        @{ Device="Logitech G Pro X Superlight"; Level="88%"; Color=[System.Drawing.Color]::FromArgb(0, 220, 160); Label="Mouse" },
        @{ Device="Razer BlackWidow V4 Pro"; Level="62%"; Color=[System.Drawing.Color]::FromArgb(0, 190, 255); Label="Keyboard" },
        @{ Device="Sony WH-1000XM5 Headset"; Level="95%"; Color=[System.Drawing.Color]::FromArgb(255, 180, 0); Label="Headset" }
    )
    $devX = $islX + 340; $devY = $islY + 45
    $fDev = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
    $fLvl = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
    foreach ($d in $items) {
        $bDev = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $g.DrawString("[$($d.Label)] $($d.Device)", $fDev, $bDev, $devX, $devY)
        $bDev.Dispose()

        $bLvl = New-Object System.Drawing.SolidBrush($d.Color)
        $g.DrawString($d.Level, $fLvl, $bLvl, ($devX + 280), $devY)
        $bLvl.Dispose()

        $devY += 48
    }
    $fDev.Dispose(); $fLvl.Dispose()

    # Hardware Monitor (CPU / RAM)
    $perfX = $islX + 710; $perfY = $islY + 45
    $fPerfH = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
    $bPerfH = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(180, 200, 230))
    $g.DrawString("CPU: 18%", $fPerfH, $bPerfH, $perfX, $perfY)
    $g.DrawString("RAM: 42%", $fPerfH, $bPerfH, $perfX, ($perfY + 48))
    $fPerfH.Dispose(); $bPerfH.Dispose()

    # Sub Caption
    $fCap = New-Object System.Drawing.Font("Segoe UI", 13)
    $bCap = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 140, 180))
    $g.DrawString("* Elegant breathing pulse alerts notify you gently before battery depletion.", $fCap, $bCap, ($islX + 48), ($islY + 290))
    $fCap.Dispose(); $bCap.Dispose()

    $outPath = Join-Path $OutputDir "Screenshot_06_OmniGlance.png"
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "Generated: $outPath"
}

# Execution
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Generating Microsoft Store 1920x1080 English Promotional Screenshots..." -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

Generate-Screenshot-Overview
Generate-Screenshot-FluidDrag
Generate-Screenshot-FocusDimmer
Generate-Screenshot-SnapTrans
Generate-Screenshot-SwiftVolume
Generate-Screenshot-OmniGlance

if ($iconImage) { $iconImage.Dispose() }
Write-Host "`nAll 6 Store Screenshots (1920x1080) successfully generated at: $OutputDir" -ForegroundColor Green
