# Store および MSIX 用の全アセット自動生成スクリプト
param (
    [string]$SourceIcon = "$PSScriptRoot/src/SanmiToys.Host/Assets/app.png"
)

Add-Type -AssemblyName System.Drawing

if (!(Test-Path $SourceIcon)) {
    Write-Error "Source icon not found at: $SourceIcon"
    exit 1
}

$source = [System.Drawing.Image]::FromFile((Resolve-Path $SourceIcon).Path)

# 出力先ディレクトリ群
$packageImagesDir = "$PSScriptRoot/src/SanmiToys.Package/Images"
$msixAssetsDir    = "$PSScriptRoot/packaging/msix/Assets"
$storeListingDir  = "$PSScriptRoot/Releases/StoreListingAssets"

foreach ($dir in @($packageImagesDir, $msixAssetsDir, $storeListingDir)) {
    if (!(Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

function Generate-Asset {
    param (
        [string]$DestinationPath,
        [int]$Width,
        [int]$Height,
        [bool]$IsCentered = $false,
        [int]$CenteredSize = 0,
        [System.Drawing.Color]$BgColor = [System.Drawing.Color]::Transparent
    )

    $bmp = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear($BgColor)

    if ($IsCentered) {
        $size = if ($CenteredSize -gt 0) { $CenteredSize } else { [Math]::Min($Width, $Height) * 0.55 }
        $x = [int](($Width - $size) / 2)
        $y = [int](($Height - $size) / 2)
        $g.DrawImage($source, $x, $y, [int]$size, [int]$size)
    } else {
        $g.DrawImage($source, 0, 0, $Width, $Height)
    }

    $g.Dispose()
    $bmp.Save($DestinationPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Generated: $DestinationPath ($($Width)x$($Height))"
}

# 1. SanmiToys.Package / Images 用アセット定義
$packageAssets = @(
    # Store Logo
    @{ Name = "StoreLogo.png"; Width = 50; Height = 50 },
    @{ Name = "StoreLogo.scale-100.png"; Width = 50; Height = 50 },
    @{ Name = "StoreLogo.scale-125.png"; Width = 63; Height = 63 },
    @{ Name = "StoreLogo.scale-150.png"; Width = 75; Height = 75 },
    @{ Name = "StoreLogo.scale-200.png"; Width = 100; Height = 100 },
    @{ Name = "StoreLogo.scale-400.png"; Width = 200; Height = 200 },

    # Square 150x150 Logo
    @{ Name = "Square150x150Logo.png"; Width = 150; Height = 150 },
    @{ Name = "Square150x150Logo.scale-100.png"; Width = 150; Height = 150 },
    @{ Name = "Square150x150Logo.scale-125.png"; Width = 188; Height = 188 },
    @{ Name = "Square150x150Logo.scale-150.png"; Width = 225; Height = 225 },
    @{ Name = "Square150x150Logo.scale-200.png"; Width = 300; Height = 300 },
    @{ Name = "Square150x150Logo.scale-400.png"; Width = 600; Height = 600 },

    # Square 44x44 Logo
    @{ Name = "Square44x44Logo.png"; Width = 44; Height = 44 },
    @{ Name = "Square44x44Logo.scale-100.png"; Width = 44; Height = 44 },
    @{ Name = "Square44x44Logo.scale-125.png"; Width = 55; Height = 55 },
    @{ Name = "Square44x44Logo.scale-150.png"; Width = 66; Height = 66 },
    @{ Name = "Square44x44Logo.scale-200.png"; Width = 88; Height = 88 },
    @{ Name = "Square44x44Logo.scale-400.png"; Width = 176; Height = 176 },

    # TargetSize unplated (タスクバー / スタートリスト用)
    @{ Name = "Square44x44Logo.targetsize-16.png"; Width = 16; Height = 16 },
    @{ Name = "Square44x44Logo.targetsize-24.png"; Width = 24; Height = 24 },
    @{ Name = "Square44x44Logo.targetsize-32.png"; Width = 32; Height = 32 },
    @{ Name = "Square44x44Logo.targetsize-48.png"; Width = 48; Height = 48 },
    @{ Name = "Square44x44Logo.targetsize-256.png"; Width = 256; Height = 256 },
    @{ Name = "Square44x44Logo.targetsize-16_altform-unplated.png"; Width = 16; Height = 16 },
    @{ Name = "Square44x44Logo.targetsize-24_altform-unplated.png"; Width = 24; Height = 24 },
    @{ Name = "Square44x44Logo.targetsize-32_altform-unplated.png"; Width = 32; Height = 32 },
    @{ Name = "Square44x44Logo.targetsize-48_altform-unplated.png"; Width = 48; Height = 48 },
    @{ Name = "Square44x44Logo.targetsize-256_altform-unplated.png"; Width = 256; Height = 256 },

    # Wide 310x150 Logo
    @{ Name = "Wide310x150Logo.png"; Width = 310; Height = 150; IsCentered = $true },
    @{ Name = "Wide310x150Logo.scale-100.png"; Width = 310; Height = 150; IsCentered = $true },
    @{ Name = "Wide310x150Logo.scale-125.png"; Width = 388; Height = 188; IsCentered = $true },
    @{ Name = "Wide310x150Logo.scale-150.png"; Width = 465; Height = 225; IsCentered = $true },
    @{ Name = "Wide310x150Logo.scale-200.png"; Width = 620; Height = 300; IsCentered = $true },
    @{ Name = "Wide310x150Logo.scale-400.png"; Width = 1240; Height = 600; IsCentered = $true },

    # SplashScreen
    @{ Name = "SplashScreen.png"; Width = 620; Height = 300; IsCentered = $true },
    @{ Name = "SplashScreen.scale-100.png"; Width = 620; Height = 300; IsCentered = $true },
    @{ Name = "SplashScreen.scale-125.png"; Width = 775; Height = 375; IsCentered = $true },
    @{ Name = "SplashScreen.scale-150.png"; Width = 930; Height = 450; IsCentered = $true },
    @{ Name = "SplashScreen.scale-200.png"; Width = 1240; Height = 600; IsCentered = $true },
    @{ Name = "SplashScreen.scale-400.png"; Width = 2480; Height = 1200; IsCentered = $true },

    # LockScreenLogo
    @{ Name = "LockScreenLogo.scale-200.png"; Width = 48; Height = 48 }
)

Write-Host "--- Generating SanmiToys.Package/Images Assets ---"
foreach ($item in $packageAssets) {
    $out = Join-Path $packageImagesDir $item.Name
    $centered = if ($item.ContainsKey("IsCentered")) { $item.IsCentered } else { $false }
    Generate-Asset -DestinationPath $out -Width $item.Width -Height $item.Height -IsCentered $centered
}

# 2. packaging/msix/Assets 用アセット定義 (スタンドアロンビルドスクリプト用)
Write-Host "`n--- Generating packaging/msix/Assets ---"
$msixAssets = @(
    @{ Name = "Square150x150Logo.png"; Width = 150; Height = 150 },
    @{ Name = "Square44x44Logo.png";   Width = 44;  Height = 44 },
    @{ Name = "StoreLogo.png";         Width = 50;  Height = 50 },
    @{ Name = "SplashScreen.png";      Width = 620; Height = 300; IsCentered = $true }
)
foreach ($item in $msixAssets) {
    $out = Join-Path $msixAssetsDir $item.Name
    $centered = if ($item.ContainsKey("IsCentered")) { $item.IsCentered } else { $false }
    Generate-Asset -DestinationPath $out -Width $item.Width -Height $item.Height -IsCentered $centered
}

# 3. Microsoft Store (Partner Center) ストア登録・掲載用画像
Write-Host "`n--- Generating Microsoft Partner Center Store Listing Assets ---"
$storeListingAssets = @(
    # アプリ アイコン (1:1)
    @{ Name = "StoreIcon_1024x1024.png"; Width = 1024; Height = 1024 },
    @{ Name = "StoreIcon_512x512.png";   Width = 512;  Height = 512 },
    @{ Name = "StoreIcon_300x300.png";   Width = 300;  Height = 300 }
)
foreach ($item in $storeListingAssets) {
    $out = Join-Path $storeListingDir $item.Name
    Generate-Asset -DestinationPath $out -Width $item.Width -Height $item.Height -IsCentered $false
}

$source.Dispose()
Write-Host "`nAll Store and Package assets have been successfully generated!"
