using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SanmiToys.Core.Services;

namespace SanmiToys.Core.Helpers;

/// <summary>
/// 画面ロック、スリープ復帰、リモートデスクトップ接続時などに DWM 構成が無効化されることで生じる
/// COMException (0x80263001: デスクトップ構成が無効化されています) を防止・抑制するヘルパー。
/// </summary>
public static class DwmCompositionHelper
{
    private const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
    private const int WM_DWMNCRENDERINGCHANGED = 0x031F;

    /// <summary>
    /// WindowChrome を使用するウィンドウ（FluentWindow 等）に対し、
    /// DWM 構成変更メッセージ (WM_DWMCOMPOSITIONCHANGED / WM_DWMNCRENDERINGCHANGED) を安全にインターセプトするフックを登録します。
    /// OnSourceInitialized 内で base.OnSourceInitialized(e) より前に呼び出すことで、
    /// WPF 内部の WindowChromeWorker より優先して安全にメッセージを処理できます。
    /// </summary>
    public static void AttachEarly(Window window)
    {
        if (window == null) return;

        try
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook(DwmMessageHook);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("DwmCompositionHelper", $"Failed to attach early DWM composition hook: {ex.Message}");
        }
    }

    /// <summary>
    /// ウィンドウに DWM 構成変更インターセプトフックを登録します。
    /// </summary>
    public static void Attach(Window window)
    {
        if (window == null) return;

        if (window.IsLoaded || PresentationSource.FromVisual(window) != null)
        {
            AttachEarly(window);
        }
        else
        {
            void OnSourceInitialized(object? sender, EventArgs e)
            {
                window.SourceInitialized -= OnSourceInitialized;
                AttachEarly(window);
            }
            window.SourceInitialized += OnSourceInitialized;
        }
    }

    private static IntPtr DwmMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DWMCOMPOSITIONCHANGED || msg == WM_DWMNCRENDERINGCHANGED)
        {
            // 画面ロック、モニタースリープ、リモートデスクトップ接続時などに DWM 構成が一時的に無効化されると、
            // WPF の WindowChromeWorker が DwmExtendFrameIntoClientArea を呼び出して
            // COMException 0x80263001 (デスクトップ構成が無効化されています) をスローする不具合を根本から防ぐ。
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 指定された例外が DWM (デスクトップウィンドウマネージャー) 構成無効化や関連する一時的な COM 例外であるかを判定します。
    /// </summary>
    public static bool IsTransientDwmException(Exception? ex)
    {
        if (ex == null) return false;

        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                if (IsTransientDwmException(inner))
                {
                    return true;
                }
            }
        }

        Exception? current = ex;
        while (current != null)
        {
            // 0x80263001: DWMERR_DESKTOP_COMPOSITION_DISABLED / UCEERR_DESKTOPCOMPOSITIONDISABLED
            if ((uint)current.HResult == 0x80263001)
            {
                return true;
            }

            if (current is COMException comEx && (uint)comEx.HResult == 0x80263001)
            {
                return true;
            }

            string msg = current.Message;
            if (!string.IsNullOrEmpty(msg))
            {
                if (msg.Contains("0x80263001", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("デスクトップ構成が無効化", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("desktop composition is disabled", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string? stack = current.StackTrace;
            if (!string.IsNullOrEmpty(stack))
            {
                if (stack.Contains("DwmExtendFrameIntoClientArea", StringComparison.OrdinalIgnoreCase) &&
                    stack.Contains("WindowChromeWorker", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            current = current.InnerException;
        }

        return false;
    }
}
