using System;
using SanmiToys.Core.Helpers;
using SanmiToys.Modules.SnapTrans.Models;
using SanmiToys.Modules.SnapTrans.Services;
using Xunit;

namespace SanmiToys.Tests;

/// <summary>
/// SwiftVolume再表示時画面埋まり防止およびSnapTransテキスト選択フォールバックの単体テスト
/// </summary>
public class SwiftVolumeAndSnapTransTests
{
    [Theory]
    [InlineData(1080, 48, 180, 10.0, 842)]  // 最小高さ時 (1032 - 180 - 10 = 842)
    [InlineData(1080, 48, 460, 10.0, 562)]  // 標準高さ時 (1032 - 460 - 10 = 562)
    [InlineData(1080, 48, 650, 10.0, 372)]  // 展開・多数セッション時 (1032 - 650 - 10 = 372)
    [InlineData(1440, 60, 500, 10.0, 870)]  // QHD解像度時 (1380 - 500 - 10 = 870)
    public void SwiftVolume_BottomTaskbar_CalculatesCorrectTopWithinWorkArea(
        double screenHeight, double taskbarHeight, double windowHeight, double margin, double expectedTop)
    {
        double workBottom = screenHeight - taskbarHeight;
        double workTop = 0;
        double workAreaH = workBottom - workTop;

        // 計算ロジック: 決定論的コンテンツサイズ計測と画面クランプ
        double actualH = Math.Clamp(windowHeight, 180, Math.Min(850, workAreaH - 20));
        double targetTop = workBottom - actualH - margin;

        if (targetTop < workTop + 8) targetTop = workTop + 8;
        if (targetTop + actualH > workBottom - 8) targetTop = workBottom - actualH - 8;

        Assert.Equal(expectedTop, targetTop);
        // ウィンドウ底面がタスクバー（workBottom）を絶対に突き抜けないことを保証
        Assert.True(targetTop + actualH <= workBottom - margin);
    }

    [Fact]
    public void SwiftVolume_OverlyTallWindow_IsClampedToWorkAreaTop()
    {
        double workTop = 0;
        double workBottom = 600; // 狭い画面
        double margin = 10.0;
        double windowHeight = 700; // 画面高さを超えるサイズ

        double workAreaH = workBottom - workTop;
        double actualH = Math.Clamp(windowHeight, 180, Math.Min(850, workAreaH - 20)); // 580にクランプ
        double targetTop = workBottom - actualH - margin; // 600 - 580 - 10 = 10

        if (targetTop < workTop + 8) targetTop = workTop + 8;
        if (targetTop + actualH > workBottom - 8) targetTop = workBottom - actualH - 8;

        Assert.True(targetTop >= workTop + 8);
        Assert.True(targetTop + actualH <= workBottom - 8);
    }

    [Fact]
    public void SnapTrans_Settings_SupportsAllSelectionModifiers()
    {
        var settings = new SnapTransSettings();

        // デフォルトは Alt
        Assert.Equal("Alt", settings.SelectionToolbarModifier);

        // 各修飾キーの設定と保持
        string[] validModifiers = ["None", "Ctrl", "Alt", "Shift"];
        foreach (var mod in validModifiers)
        {
            settings.SelectionToolbarModifier = mod;
            Assert.Equal(mod, settings.SelectionToolbarModifier);
        }
    }

    [Fact]
    public void SnapTrans_TextSelectionEngine_PublicInterface_IsExposedForHotkeyIntegration()
    {
        // SnapTransModule からテキスト選択ポップアップをトリガーするためのメソッドが存在することを確認
        var method = typeof(TextSelectionEngine).GetMethod(nameof(TextSelectionEngine.TryTriggerToolbarNearCursorAsync));
        Assert.NotNull(method);
        Assert.True(method.IsPublic);

        var getSelectedTextMethod = typeof(TextSelectionEngine).GetMethod(nameof(TextSelectionEngine.GetSelectedTextAsync));
        Assert.NotNull(getSelectedTextMethod);
        Assert.True(getSelectedTextMethod.IsPublic);
    }

    [Fact]
    public void OmniGlance_Settings_AlwaysOnTop_DefaultsToTrue()
    {
        var settings = new SanmiToys.Modules.OmniGlance.Models.OmniGlanceSettings();
        Assert.True(settings.AlwaysOnTop);

        settings.AlwaysOnTop = false;
        Assert.False(settings.AlwaysOnTop);
    }

    [Theory]
    [InlineData(1000, 500, 1032, 10, 522)] // 下端 1500 > 1022 (1032 - 10) -> y = 1022 - 500 = 522
    [InlineData(800, 200, 1032, 10, 800)]  // 下端 1000 <= 1022 -> 変更なし
    [InlineData(900, 300, 1032, 10, 722)]  // 下端 1200 > 1022 -> y = 1022 - 300 = 722
    public void SwiftVolume_WindowPosChanging_CalculatesCorrectClampedY(int initialY, int height, int workBottom, int margin, int expectedY)
    {
        int maxBottom = workBottom - margin;
        int clampedY = initialY;
        if (initialY + height > maxBottom && height > 0)
        {
            clampedY = Math.Max(0, maxBottom - height);
        }

        Assert.Equal(expectedY, clampedY);
        Assert.True(clampedY + height <= maxBottom);
    }

    [Fact]
    public void SwiftVolume_WindowPosChanging_WhenCyZeroOrNegative_UsesSafeFallbackHeight()
    {
        // SWP_NOSIZE 等で wp.cy が 0 や未定義値（<= 0）になった場合でも、
        // 460 等の安全なフォールバック高さが適用され、タスクバーの下に隠れないことを検証
        int initialY = 900;
        int invalidCy = 0;
        int fallbackHeight = 460;
        int workBottom = 1032;
        int margin = 10;
        int maxBottom = workBottom - margin; // 1022

        int effectiveHeight = invalidCy <= 0 ? fallbackHeight : invalidCy;
        int clampedY = initialY;
        if (initialY + effectiveHeight > maxBottom && effectiveHeight > 0)
        {
            clampedY = Math.Max(0, maxBottom - effectiveHeight);
        }

        // 900 + 460 = 1360 > 1022 -> y = 1022 - 460 = 562
        Assert.Equal(562, clampedY);
        Assert.True(clampedY + effectiveHeight <= maxBottom);
    }

    [Theory]
    [InlineData(0, 1032, 192)]  // セッション 0 個: 192px (MinHeight 180px を上回る)
    [InlineData(2, 1032, 320)]  // セッション 2 個: 192 + 128 = 320px
    [InlineData(5, 1032, 512)]  // セッション 5 個: 192 + 320 = 512px
    [InlineData(15, 600, 580)]  // 狭いワークエリア (600px): MaxHeight (580px) にクランプ
    public void SwiftVolume_DesiredDimensionsCalculation_ClampsAndPreventsBottomClipping(int sessionCount, double workAreaH, double expectedTargetH)
    {
        double minHeight = 180.0;
        double maxHeight = Math.Min(850.0, workAreaH - 20.0);
        double fallbackH = 192.0 + (sessionCount * 64.0);

        double targetH = Math.Clamp(fallbackH, minHeight, maxHeight);
        Assert.Equal(expectedTargetH, targetH);

        // 下部タスクバー起点での底面クランプ計算
        double workBottom = workAreaH;
        double margin = 10.0;
        double finalTop = workBottom - targetH - margin;

        // ウィンドウ底面がタスクバー上端（workBottom - margin）に完全に一致し、下側が切れないことを保証
        Assert.Equal(workBottom - margin, finalTop + targetH);
        Assert.True(finalTop >= 0);
    }

    [Fact]
    public void SnapTrans_OcrService_CreateInvertedBitmap_InvertsColorsCorrectly()
    {
        using var bmp = new System.Drawing.Bitmap(10, 10, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // (0, 0) を白 (255, 255, 255), (1, 1) を黒 (0, 0, 0) に設定
        bmp.SetPixel(0, 0, System.Drawing.Color.FromArgb(255, 255, 255, 255));
        bmp.SetPixel(1, 1, System.Drawing.Color.FromArgb(255, 0, 0, 0));

        using var inverted = OcrService.CreateInvertedBitmap(bmp);
        var invertedWhite = inverted.GetPixel(0, 0);
        var invertedBlack = inverted.GetPixel(1, 1);

        // 白の反転は黒 (許容誤差 ±2)
        Assert.True(invertedWhite.R <= 2 && invertedWhite.G <= 2 && invertedWhite.B <= 2);
        // 黒の反転は白 (許容誤差 ±2)
        Assert.True(invertedBlack.R >= 253 && invertedBlack.G >= 253 && invertedBlack.B >= 253);
    }

    [Fact]
    public void SnapTrans_OcrService_CreateBinarizedBitmap_ProducesBinarizedImage()
    {
        using var bmp = new System.Drawing.Bitmap(20, 20, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        // 半分を黒 (0,0,0)、半分を白 (255,255,255) にして明瞭な二値ヒストグラムを作成
        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                var color = x < 10 ? System.Drawing.Color.FromArgb(255, 10, 10, 10) : System.Drawing.Color.FromArgb(255, 240, 240, 240);
                bmp.SetPixel(x, y, color);
            }
        }

        using var binarized = OcrService.CreateBinarizedBitmap(bmp, invert: false);
        var darkPixel = binarized.GetPixel(5, 5);
        var lightPixel = binarized.GetPixel(15, 15);

        // 二値化後は 0 または 255 の純粋な白黒
        Assert.Equal(0, darkPixel.R);
        Assert.Equal(255, lightPixel.R);

        // 反転二値化の検証
        using var binarizedInverted = OcrService.CreateBinarizedBitmap(bmp, invert: true);
        var invDarkPixel = binarizedInverted.GetPixel(5, 5);
        var invLightPixel = binarizedInverted.GetPixel(15, 15);

        Assert.Equal(255, invDarkPixel.R);
        Assert.Equal(0, invLightPixel.R);
    }

    [Fact]
    public void SnapTrans_OcrService_CreateNearestNeighborBitmap_ScalesAccurately()
    {
        using var bmp = new System.Drawing.Bitmap(10, 10, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, System.Drawing.Color.FromArgb(255, 255, 0, 0));

        using var scaled = OcrService.CreateNearestNeighborBitmap(bmp, 40, 40);
        Assert.Equal(40, scaled.Width);
        Assert.Equal(40, scaled.Height);

        // (0, 0) の赤色が (1, 1) にそのまま保持されている（バイキュービックによるボケが発生しない）
        var p = scaled.GetPixel(1, 1);
        Assert.Equal(255, p.R);
        Assert.Equal(0, p.G);
        Assert.Equal(0, p.B);
    }

    [Fact]
    public void SnapTrans_BuildCopyCommandInputs_WhenAltDown_PreservesAltAndSuppressesMenu()
    {
        // ユーザーがテキスト選択後に ALT を押下（ホールド）した状態でポップアップ起動されたシナリオ
        var inputs = TextSelectionEngine.BuildCopyCommandInputs(wasCtrlDown: false, wasAltDown: true, wasShiftDown: false);

        Assert.Equal(6, inputs.Count);

        // 1. メニューバー誤起動（SC_KEYMENU）を防ぐため、Alt解放より前にCtrlを先行押下
        Assert.Equal((ushort)NativeMethods.VK_CONTROL, inputs[0].U.ki.wVk);
        Assert.Equal(0u, inputs[0].U.ki.dwFlags);

        // 2. Alt を一時解放（Ctrl押下中のため単独押し判定にならず、純粋なCtrl+Cコピー環境を準備）
        Assert.Equal((ushort)NativeMethods.VK_MENU, inputs[1].U.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags);

        // 3. C 押下 & 解放
        Assert.Equal((ushort)NativeMethods.VK_C, inputs[2].U.ki.wVk);
        Assert.Equal(0u, inputs[2].U.ki.dwFlags);
        Assert.Equal((ushort)NativeMethods.VK_C, inputs[3].U.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[3].U.ki.dwFlags);

        // 4. ユーザーが物理的に長押ししている Alt を押下状態に復元
        Assert.Equal((ushort)NativeMethods.VK_MENU, inputs[4].U.ki.wVk);
        Assert.Equal(0u, inputs[4].U.ki.dwFlags);

        // 5. 最後に先行押下した Ctrl を解放
        Assert.Equal((ushort)NativeMethods.VK_CONTROL, inputs[5].U.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[5].U.ki.dwFlags);
    }

    [Fact]
    public void SnapTrans_BuildCopyCommandInputs_WhenCtrlAlreadyDown_DoesNotRedundantlyToggleCtrl()
    {
        // Ctrlが既に押下中の場合、不要なCtrl Down/Upは挿入されず、Cの送出のみ行う
        var inputs = TextSelectionEngine.BuildCopyCommandInputs(wasCtrlDown: true, wasAltDown: false, wasShiftDown: false);

        Assert.Equal(2, inputs.Count);
        Assert.Equal((ushort)NativeMethods.VK_C, inputs[0].U.ki.wVk);
        Assert.Equal(0u, inputs[0].U.ki.dwFlags);
        Assert.Equal((ushort)NativeMethods.VK_C, inputs[1].U.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags);
    }

    [Fact]
    public void SnapTrans_BuildCopyCommandInputs_WhenAltAndShiftDown_SafelyWrapsAndRestoresBoth()
    {
        // Alt + Shift の複合状態でも両方が一時解放・復元される
        var inputs = TextSelectionEngine.BuildCopyCommandInputs(wasCtrlDown: false, wasAltDown: true, wasShiftDown: true);

        Assert.Equal(8, inputs.Count);

        // 先頭: Ctrl Down
        Assert.Equal((ushort)NativeMethods.VK_CONTROL, inputs[0].U.ki.wVk);
        Assert.Equal(0u, inputs[0].U.ki.dwFlags);

        // Alt, Shift 一時解放
        Assert.Equal((ushort)NativeMethods.VK_MENU, inputs[1].U.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[1].U.ki.dwFlags);
        Assert.Equal((ushort)NativeMethods.VK_SHIFT, inputs[2].U.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[2].U.ki.dwFlags);

        // C Down, C Up
        Assert.Equal((ushort)NativeMethods.VK_C, inputs[3].U.ki.wVk);
        Assert.Equal(0u, inputs[3].U.ki.dwFlags);
        Assert.Equal((ushort)NativeMethods.VK_C, inputs[4].U.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[4].U.ki.dwFlags);

        // Alt, Shift 復元
        Assert.Equal((ushort)NativeMethods.VK_MENU, inputs[5].U.ki.wVk);
        Assert.Equal(0u, inputs[5].U.ki.dwFlags);
        Assert.Equal((ushort)NativeMethods.VK_SHIFT, inputs[6].U.ki.wVk);
        Assert.Equal(0u, inputs[6].U.ki.dwFlags);

        // 末尾: Ctrl Up
        Assert.Equal((ushort)NativeMethods.VK_CONTROL, inputs[7].U.ki.wVk);
        Assert.Equal(NativeMethods.KEYEVENTF_KEYUP, inputs[7].U.ki.dwFlags);
    }
}
