using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SanmiToys.Core.Helpers;

/// <summary>
/// Translucent Windows、TranslucentTB、DWMアクリル注入ツール等との干渉を防ぎ、
/// 透明ポップアップ/HUDウィンドウの枠外に不要なブラーが描画されるのを防止する互換性ヘルパー。
/// </summary>
public static class WindowBackdropCompatibilityHelper
{
    public static void EnsureTransparentPopupCompatibility(Window window)
    {
        if (window == null) return;

        if (window.IsLoaded)
        {
            ApplyToWindow(window);
        }
        else
        {
            window.SourceInitialized += (s, e) =>
            {
                ApplyToWindow(window);
            };
        }
    }

    private static void ApplyToWindow(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 1. DWM システムバックドロップ (Mica / Acrylic) を明示的に無効化 (DWMSBT_NONE = 1)
            int backdrop = NativeMethods.DWMSBT_NONE;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // 2. DWM 自動角丸 (DWMWCP_DONOTROUND = 1) を適用（WPF の CornerRadius との重複・余白矩形ブラーを防止）
            int doNotRound = NativeMethods.DWMWCP_DONOTROUND;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));

            // 3. TranslucentTB 等のフックツールによる AccentPolicy (BlurBehind/Acrylic) の強制適用を上書き無効化 (ACCENT_DISABLED = 0)
            var accent = new NativeMethods.AccentPolicy
            {
                AccentState = NativeMethods.AccentState.ACCENT_DISABLED,
                AccentFlags = 0,
                GradientColor = 0,
                AnimationId = 0
            };
            int accentStructSize = Marshal.SizeOf(accent);
            IntPtr accentPtr = Marshal.AllocHGlobal(accentStructSize);
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new NativeMethods.WindowCompositionAttributeData
                {
                    Attribute = 19, // WCA_ACCENT_POLICY
                    Data = accentPtr,
                    SizeOfData = accentStructSize
                };
                NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }
        catch { }
    }
}
