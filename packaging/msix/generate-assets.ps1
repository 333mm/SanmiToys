param (
    [string]$SourceIcon = "$PSScriptRoot/../../src/SanmiToys.Host/Assets/app.png",
    [string]$OutputDir = "$PSScriptRoot/Assets"
)

Add-Type -AssemblyName System.Drawing

if (!(Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

$source = [System.Drawing.Image]::FromFile((Resolve-Path $SourceIcon).Path)

$assets = @(
    @{ Name = "Square150x150Logo.png"; Width = 150; Height = 150; IsCentered = $false },
    @{ Name = "Square44x44Logo.png";   Width = 44;  Height = 44;  IsCentered = $false },
    @{ Name = "StoreLogo.png";         Width = 50;  Height = 50;  IsCentered = $false },
    @{ Name = "SplashScreen.png";      Width = 620; Height = 300; IsCentered = $true }
)

foreach ($item in $assets) {
    $bmp = New-Object System.Drawing.Bitmap($item.Width, $item.Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    if ($item.IsCentered) {
        $iconSize = 150
        $x = [int](($item.Width - $iconSize) / 2)
        $y = [int](($item.Height - $iconSize) / 2)
        $g.DrawImage($source, $x, $y, $iconSize, $iconSize)
    } else {
        $g.DrawImage($source, 0, 0, $item.Width, $item.Height)
    }
    $g.Dispose()

    $outPath = Join-Path $OutputDir $item.Name
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Created asset: $outPath"
}

$source.Dispose()
Write-Host "MSIX assets generation completed."
