# ==============================================================================
# SanmiToys - Microsoft Store 実際のアプリスクリーンショット取得スクリプト
# ==============================================================================
# Microsoft Store ポリシー 10.1.1.3 (Inaccurate Representation) に準拠するため、
# 実際のアプリを起動して画面を直接キャプチャします。
#
# 使い方:
#   .\capture-store-screenshots.ps1
#   .\capture-store-screenshots.ps1 -AppExe ".\path\to\SanmiToys.exe"
#   .\capture-store-screenshots.ps1 -OutputDir ".\MyScreenshots"
# ==============================================================================

param (
    [string]$AppExe    = "$PSScriptRoot\src\SanmiToys.Host\bin\Release\net8.0-windows10.0.19041.0\SanmiToys.Host.exe",
    [string]$OutputDir = "$PSScriptRoot\Releases\StoreListingAssets\Screenshots"
)

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

# Win32 API: ウィンドウ操作用
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

if (!(Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

# スクリーンショット撮影関数
function Capture-Window {
    param (
        [System.Diagnostics.Process]$proc,
        [string]$outPath,
        [string]$label
    )
    Start-Sleep -Milliseconds 800

    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) {
        Write-Warning "[$label] ウィンドウハンドルが見つかりません"
        return
    }

    # ウィンドウを前面表示
    [Win32]::ShowWindow($hwnd, 9)  # SW_RESTORE
    [Win32]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 600

    $rect = New-Object Win32+RECT
    [Win32]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top

    if ($w -le 0 -or $h -le 0) {
        Write-Warning "[$label] ウィンドウサイズが無効です ($w x $h)"
        return
    }

    # スクリーンキャプチャ
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $g.Dispose()

    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  キャプチャ完了: $outPath ($w x $h)" -ForegroundColor Green
}

# アプリ起動
if (!(Test-Path $AppExe)) {
    Write-Error "アプリが見つかりません: $AppExe`nビルド後に実行してください: dotnet build src\SanmiToys.Host\SanmiToys.Host.csproj -c Release"
    exit 1
}

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " SanmiToys ストア用スクリーンショット取得" -ForegroundColor Cyan
Write-Host " ※ Microsoft Store ポリシー 10.1.1.3 準拠 (実アプリキャプチャ)" -ForegroundColor Cyan
Write-Host "========================================================`n" -ForegroundColor Cyan

Write-Host "アプリを起動しています..." -ForegroundColor Yellow
$proc = Start-Process -FilePath $AppExe -PassThru
Start-Sleep -Seconds 4  # 起動待機

if ($proc.HasExited) {
    Write-Error "アプリの起動に失敗しました (終了コード: $($proc.ExitCode))"
    exit 1
}

$proc.Refresh()
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
    Write-Warning "MainWindowHandle が取得できません。少し待ちます..."
    Start-Sleep -Seconds 3
    $proc.Refresh()
}

# ウィンドウサイズを固定 (1280x800) ※ストア推奨サイズに近い
[Win32]::ShowWindow($proc.MainWindowHandle, 9) | Out-Null
[Win32]::MoveWindow($proc.MainWindowHandle, 100, 50, 1280, 800, $true) | Out-Null
Start-Sleep -Milliseconds 500

Write-Host "`n各ページのスクリーンショットを取得します" -ForegroundColor Yellow
Write-Host "注意: スクリーンショット取得中は他の操作を行わないでください`n" -ForegroundColor DarkYellow

# --- 1. Dashboard (概要) ---
Write-Host "[1/6] Dashboard ページ" -ForegroundColor Cyan
[Win32]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
# Dashboard は起動時のデフォルトページ
Start-Sleep -Milliseconds 1500
Capture-Window $proc (Join-Path $OutputDir "Screenshot_01_Dashboard.png") "Dashboard"

# --- 2. FluidDrag ---
Write-Host "[2/6] FluidDrag 設定ページ" -ForegroundColor Cyan
[System.Windows.Forms.SendKeys]::SendWait("")
Start-Sleep -Milliseconds 300
Write-Host "  -> アプリの左側ナビゲーションから 'FluidDrag' をクリックしてください..." -ForegroundColor DarkYellow
Write-Host "     3秒後に自動でキャプチャします" -ForegroundColor DarkYellow
Start-Sleep -Seconds 3
Capture-Window $proc (Join-Path $OutputDir "Screenshot_02_FluidDrag.png") "FluidDrag"

# --- 3. FocusDimmer ---
Write-Host "[3/6] FocusDimmer 設定ページ" -ForegroundColor Cyan
Write-Host "  -> アプリの左側ナビゲーションから 'FocusDimmer' をクリックしてください..." -ForegroundColor DarkYellow
Write-Host "     3秒後に自動でキャプチャします" -ForegroundColor DarkYellow
Start-Sleep -Seconds 3
Capture-Window $proc (Join-Path $OutputDir "Screenshot_03_FocusDimmer.png") "FocusDimmer"

# --- 4. SnapTrans ---
Write-Host "[4/6] SnapTrans 設定ページ" -ForegroundColor Cyan
Write-Host "  -> アプリの左側ナビゲーションから 'SnapTrans' をクリックしてください..." -ForegroundColor DarkYellow
Write-Host "     3秒後に自動でキャプチャします" -ForegroundColor DarkYellow
Start-Sleep -Seconds 3
Capture-Window $proc (Join-Path $OutputDir "Screenshot_04_SnapTrans.png") "SnapTrans"

# --- 5. SwiftVolume ---
Write-Host "[5/6] SwiftVolume 設定ページ" -ForegroundColor Cyan
Write-Host "  -> アプリの左側ナビゲーションから 'SwiftVolume' をクリックしてください..." -ForegroundColor DarkYellow
Write-Host "     3秒後に自動でキャプチャします" -ForegroundColor DarkYellow
Start-Sleep -Seconds 3
Capture-Window $proc (Join-Path $OutputDir "Screenshot_05_SwiftVolume.png") "SwiftVolume"

# --- 6. OmniGlance ---
Write-Host "[6/6] OmniGlance 設定ページ" -ForegroundColor Cyan
Write-Host "  -> アプリの左側ナビゲーションから 'OmniGlance' をクリックしてください..." -ForegroundColor DarkYellow
Write-Host "     3秒後に自動でキャプチャします" -ForegroundColor DarkYellow
Start-Sleep -Seconds 3
Capture-Window $proc (Join-Path $OutputDir "Screenshot_06_OmniGlance.png") "OmniGlance"

Write-Host "`n========================================================" -ForegroundColor Cyan
Write-Host " 完了! 全 6 枚のスクリーンショットを保存しました:" -ForegroundColor Green
Write-Host "  $OutputDir" -ForegroundColor Green
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "次のステップ:" -ForegroundColor Yellow
Write-Host "  1. 上記フォルダを開いてスクリーンショットの内容を確認してください"
Write-Host "  2. 必要に応じて手動で再撮影・トリミングを行ってください"
Write-Host "  3. パートナーセンターのストアリストに各言語ごとにアップロードしてください"
Write-Host ""
Write-Host "注意: Microsoft Store ポリシー 10.1.1.3 では、" -ForegroundColor DarkYellow
Write-Host "      スクリーンショットは実際のアプリ画面の直接キャプチャである必要があります。" -ForegroundColor DarkYellow
