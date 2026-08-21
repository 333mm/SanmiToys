using System;
using System.Runtime.InteropServices;
using System.Windows;
using SanmiToys.Modules.SwiftVolume.Core;

namespace SanmiToys.Modules.SwiftVolume.Helpers;

public static class WindowPlacementHelper
{
    public static (double Left, double Top) CalculatePositionNearCursor(double windowWidth, double windowHeight, double margin = 12.0, double padding = 8.0)
    {
        SwiftVolumeNativeMethods.GetCursorPos(out var p);

        double dpiScaleX = 1.0;
        double dpiScaleY = 1.0;

        SwiftVolumeNativeMethods.RECT rcMonitor = new SwiftVolumeNativeMethods.RECT 
        { 
            Left = 0, 
            Top = 0, 
            Right = (int)SystemParameters.PrimaryScreenWidth, 
            Bottom = (int)SystemParameters.PrimaryScreenHeight 
        };
        SwiftVolumeNativeMethods.RECT rcWork = new SwiftVolumeNativeMethods.RECT 
        { 
            Left = 0, 
            Top = 0, 
            Right = (int)SystemParameters.WorkArea.Width, 
            Bottom = (int)SystemParameters.WorkArea.Height 
        };

        try
        {
            IntPtr hMonitor = SwiftVolumeNativeMethods.MonitorFromPoint(p, SwiftVolumeNativeMethods.MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                if (SwiftVolumeNativeMethods.GetDpiForMonitor(hMonitor, SwiftVolumeNativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
                {
                    dpiScaleX = dpiX / 96.0;
                    dpiScaleY = dpiY / 96.0;
                }

                SwiftVolumeNativeMethods.MONITORINFO mi = new SwiftVolumeNativeMethods.MONITORINFO 
                { 
                    cbSize = Marshal.SizeOf<SwiftVolumeNativeMethods.MONITORINFO>() 
                };
                if (SwiftVolumeNativeMethods.GetMonitorInfo(hMonitor, ref mi))
                {
                    rcMonitor = mi.rcMonitor;
                    rcWork = mi.rcWork;
                }
            }
        }
        catch { }

        double w = windowWidth > 0 ? windowWidth : 350;
        double h = windowHeight > 0 ? windowHeight : 450;

        // タスクバーの配置判定
        bool tbLeft = rcWork.Left > rcMonitor.Left;
        bool tbTop = rcWork.Top > rcMonitor.Top;
        bool tbRight = rcWork.Right < rcMonitor.Right;
        bool tbBottom = rcWork.Bottom < rcMonitor.Bottom;

        // 論理ピクセル変換
        double workLeft = rcWork.Left / dpiScaleX;
        double workTop = rcWork.Top / dpiScaleY;
        double workRight = rcWork.Right / dpiScaleX;
        double workBottom = rcWork.Bottom / dpiScaleY;

        double cursorX = p.X / dpiScaleX;
        double cursorY = p.Y / dpiScaleY;

        double finalLeft;
        double finalTop;

        if (tbBottom)
        {
            finalTop = workBottom - h - margin;
            finalLeft = cursorX - (w / 2);
        }
        else if (tbTop)
        {
            finalTop = workTop + margin;
            finalLeft = cursorX - (w / 2);
        }
        else if (tbLeft)
        {
            finalLeft = workLeft + margin;
            finalTop = cursorY - (h / 2);
        }
        else if (tbRight)
        {
            finalLeft = workRight - w - margin;
            finalTop = cursorY - (h / 2);
        }
        else
        {
            finalTop = workBottom - h - margin;
            finalLeft = cursorX - (w / 2);
        }

        // ワークエリア内にクランプ
        if (finalLeft < workLeft + padding) finalLeft = workLeft + padding;
        if (finalLeft + w > workRight - padding) finalLeft = workRight - w - padding;

        if (finalTop < workTop + padding) finalTop = workTop + padding;
        if (finalTop + h > workBottom - padding) finalTop = workBottom - h - padding;

        return (finalLeft, finalTop);
    }

    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        uint foreThread = SwiftVolumeNativeMethods.GetWindowThreadProcessId(SwiftVolumeNativeMethods.GetForegroundWindow(), out _);
        uint appThread = SwiftVolumeNativeMethods.GetCurrentThreadId();
        bool needsAttach = foreThread != appThread && foreThread != 0;

        try
        {
            if (needsAttach)
            {
                SwiftVolumeNativeMethods.AttachThreadInput(foreThread, appThread, true);
            }

            bool sfwResult = SwiftVolumeNativeMethods.SetForegroundWindow(hwnd);
            if (!sfwResult)
            {
                SwiftVolumeNativeMethods.SwitchToThisWindow(hwnd, true);
            }

            SwiftVolumeNativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwiftVolumeNativeMethods.SWP_NOMOVE | SwiftVolumeNativeMethods.SWP_NOSIZE | SwiftVolumeNativeMethods.SWP_SHOWWINDOW);
        }
        catch { }
        finally
        {
            if (needsAttach)
            {
                SwiftVolumeNativeMethods.AttachThreadInput(foreThread, appThread, false);
            }
        }
    }
}
