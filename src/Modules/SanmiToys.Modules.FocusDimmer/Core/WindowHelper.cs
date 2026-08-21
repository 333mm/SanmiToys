using System;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace SanmiToys.Modules.FocusDimmer.Core;

public static class WindowHelper
{
    public static bool IsMenuOrPopupEx(IntPtr hwnd)
    {
        if (!FocusDimmerNativeMethods.GetWindowRect(hwnd, out FocusDimmerNativeMethods.RECT r)) return false;
        if (r.Right - r.Left <= 1 || r.Bottom - r.Top <= 1) return false;

        StringBuilder sb = new StringBuilder(256);
        FocusDimmerNativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();

        if (cls.Contains("SnapLayout") || cls == "MagUIClass") return false;

        if (IsSystemMenuOrPopupClass(cls))
        {
            if (cls == "Windows.UI.Core.CoreWindow")
            {
                double w = r.Right - r.Left;
                double h = r.Bottom - r.Top;
                double screenW = SystemParameters.PrimaryScreenWidth;
                double screenH = SystemParameters.PrimaryScreenHeight;
                if (w > screenW * 0.8 && h > screenH * 0.8) return false;
                return true;
            }
            return true;
        }

        FocusDimmerNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string procName = ProcessInfoHelper.GetProcessName(pid);
        if (procName == "explorer")
        {
            int style = FocusDimmerNativeMethods.GetWindowLong(hwnd, FocusDimmerNativeMethods.GWL_STYLE);
            if ((style & FocusDimmerNativeMethods.WS_POPUP) != 0)
            {
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
                return true;
            }
        }
        return false;
    }

    public static void DisableBackdropAndBlur(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22621)
            {
                int backdropType = FocusDimmerNativeMethods.DWMSBT_NONE;
                FocusDimmerNativeMethods.DwmSetWindowAttribute(hwnd, FocusDimmerNativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            }
            else if (Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000)
            {
                int micaVal = 0;
                FocusDimmerNativeMethods.DwmSetWindowAttribute(hwnd, FocusDimmerNativeMethods.DWMWA_MICA_EFFECT, ref micaVal, sizeof(int));
            }

            var margins = new FocusDimmerNativeMethods.MARGINS { cxLeftWidth = 0, cxRightWidth = 0, cyTopHeight = 0, cyBottomHeight = 0 };
            FocusDimmerNativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

            var accent = new FocusDimmerNativeMethods.AccentPolicy { AccentState = FocusDimmerNativeMethods.AccentState.ACCENT_DISABLED };
            var accentStructSize = System.Runtime.InteropServices.Marshal.SizeOf(accent);
            var accentPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(accentStructSize);
            System.Runtime.InteropServices.Marshal.StructureToPtr(accent, accentPtr, false);

            var data = new FocusDimmerNativeMethods.WindowCompositionAttributeData
            {
                Attribute = FocusDimmerNativeMethods.WindowCompositionAttribute.WCA_ACCENT_POLICY,
                SizeOfData = accentStructSize,
                Data = accentPtr
            };

            FocusDimmerNativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            System.Runtime.InteropServices.Marshal.FreeHGlobal(accentPtr);
        }
        catch { }
    }

    private static bool IsSystemMenuOrPopupClass(string cls)
    {
        return cls is "#32768" or "ComboLBox" or "DropDown" or "Windows.UI.Core.CoreWindow" or "Xaml_WindowedPopupClass";
    }
}
