using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using SanmiToys.Core.Services;

namespace SanmiToys.Modules.OmniGlance.Helpers;

public class OmniScreenItem
{
    public string DeviceName { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public ScreenDpiBounds Bounds { get; set; } = new();
}

public class ScreenDpiBounds
{
    public string DeviceName { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }

    public double ScreenLeft { get; set; }
    public double ScreenTop { get; set; }
    public double ScreenWidth { get; set; }
    public double ScreenHeight { get; set; }

    public double WorkLeft { get; set; }
    public double WorkTop { get; set; }
    public double WorkRight { get; set; }
    public double WorkBottom { get; set; }
    public double WorkWidth => WorkRight - WorkLeft;
    public double WorkHeight => WorkBottom - WorkTop;

    public double DpiScaleX { get; set; } = 1.0;
    public double DpiScaleY { get; set; } = 1.0;
}

public static class OmniScreenHelper
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private delegate bool MonitorEnumDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumDelegate lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const int MDT_EFFECTIVE_DPI = 0;

    /// <summary>
    /// 接続されている全モニターの一覧を取得（プライマリを先頭にする）
    /// </summary>
    public static List<OmniScreenItem> GetAllScreens(bool allowTaskbarPlacement = false)
    {
        var screens = new List<ScreenDpiBounds>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref RECT rc, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                double dpiScaleX = 1.0;
                double dpiScaleY = 1.0;
                if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dpiX, out uint dpiY) == 0)
                {
                    dpiScaleX = Math.Max(0.5, dpiX / 96.0);
                    dpiScaleY = Math.Max(0.5, dpiY / 96.0);
                }

                double sLeft = mi.rcMonitor.Left / dpiScaleX;
                double sTop = mi.rcMonitor.Top / dpiScaleY;
                double sWidth = (mi.rcMonitor.Right - mi.rcMonitor.Left) / dpiScaleX;
                double sHeight = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / dpiScaleY;

                double wLeft = mi.rcWork.Left / dpiScaleX;
                double wTop = mi.rcWork.Top / dpiScaleY;
                double wRight = mi.rcWork.Right / dpiScaleX;
                double wBottom = allowTaskbarPlacement ? (sTop + sHeight) : (mi.rcWork.Bottom / dpiScaleY);

                bool isPrimary = (mi.dwFlags & 1) != 0;

                screens.Add(new ScreenDpiBounds
                {
                    DeviceName = mi.szDevice ?? string.Empty,
                    IsPrimary = isPrimary,
                    ScreenLeft = sLeft,
                    ScreenTop = sTop,
                    ScreenWidth = sWidth,
                    ScreenHeight = sHeight,
                    WorkLeft = wLeft,
                    WorkTop = wTop,
                    WorkRight = wRight,
                    WorkBottom = wBottom,
                    DpiScaleX = dpiScaleX,
                    DpiScaleY = dpiScaleY
                });
            }
            return true;
        }, IntPtr.Zero);

        var loc = LocalizationService.Instance;
        string primaryLabel = loc["FocusDimmer_Monitor_Primary"];
        string secondaryLabel = loc["FocusDimmer_Monitor_Secondary"];

        var list = new List<OmniScreenItem>();
        foreach (var sc in screens.OrderByDescending(s => s.IsPrimary))
        {
            string typeLabel = sc.IsPrimary ? primaryLabel : secondaryLabel;
            string devClean = sc.DeviceName.Replace(@"\\.\", "");
            string friendlyName = $"{typeLabel} ({(int)sc.ScreenWidth}×{(int)sc.ScreenHeight}) - {devClean}";

            list.Add(new OmniScreenItem
            {
                DeviceName = sc.DeviceName,
                FriendlyName = friendlyName,
                IsPrimary = sc.IsPrimary,
                Bounds = sc
            });
        }

        return list;
    }

    /// <summary>
    /// 対象モニターの DPI 基準 Bounds を取得（指定なしまたは見つからない場合はプライマリ画面）
    /// </summary>
    public static ScreenDpiBounds GetTargetScreenBounds(string? targetDeviceName, bool allowTaskbarPlacement = false)
    {
        var screens = GetAllScreens(allowTaskbarPlacement);
        if (!string.IsNullOrEmpty(targetDeviceName))
        {
            var match = screens.FirstOrDefault(s => s.DeviceName.Equals(targetDeviceName, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match.Bounds;
        }

        var primary = screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
        if (primary != null) return primary.Bounds;

        return new ScreenDpiBounds
        {
            ScreenWidth = System.Windows.SystemParameters.PrimaryScreenWidth,
            ScreenHeight = System.Windows.SystemParameters.PrimaryScreenHeight,
            WorkLeft = System.Windows.SystemParameters.WorkArea.Left,
            WorkTop = System.Windows.SystemParameters.WorkArea.Top,
            WorkRight = System.Windows.SystemParameters.WorkArea.Right,
            WorkBottom = allowTaskbarPlacement ? System.Windows.SystemParameters.PrimaryScreenHeight : System.Windows.SystemParameters.WorkArea.Bottom
        };
    }

    /// <summary>
    /// DIP 座標の点から、最も近いまたは内包するモニター Bounds を判定
    /// </summary>
    public static ScreenDpiBounds FindScreenAtDipPoint(double dipX, double dipY, bool allowTaskbarPlacement = false)
    {
        var screens = GetAllScreens(allowTaskbarPlacement);
        foreach (var s in screens)
        {
            var b = s.Bounds;
            if (dipX >= b.ScreenLeft && dipX <= b.ScreenLeft + b.ScreenWidth &&
                dipY >= b.ScreenTop && dipY <= b.ScreenTop + b.ScreenHeight)
            {
                return b;
            }
        }

        if (screens.Count > 0)
        {
            ScreenDpiBounds closest = screens[0].Bounds;
            double minDistanceSq = double.MaxValue;
            foreach (var s in screens)
            {
                var b = s.Bounds;
                double cx = b.ScreenLeft + b.ScreenWidth / 2.0;
                double cy = b.ScreenTop + b.ScreenHeight / 2.0;
                double distSq = (dipX - cx) * (dipX - cx) + (dipY - cy) * (dipY - cy);
                if (distSq < minDistanceSq)
                {
                    minDistanceSq = distSq;
                    closest = b;
                }
            }
            return closest;
        }

        return GetTargetScreenBounds(null, allowTaskbarPlacement);
    }

    /// <summary>
    /// 全画面を結合した仮想デスクトップの DIP 矩形を取得 (ドラッグ可動範囲)
    /// </summary>
    public static (double Left, double Top, double Right, double Bottom) GetVirtualScreenDipBounds()
    {
        var screens = GetAllScreens(true);
        if (screens.Count == 0)
        {
            return (0, 0, System.Windows.SystemParameters.PrimaryScreenWidth, System.Windows.SystemParameters.PrimaryScreenHeight);
        }

        double minLeft = double.MaxValue;
        double minTop = double.MaxValue;
        double maxRight = double.MinValue;
        double maxBottom = double.MinValue;

        foreach (var s in screens)
        {
            var b = s.Bounds;
            if (b.ScreenLeft < minLeft) minLeft = b.ScreenLeft;
            if (b.ScreenTop < minTop) minTop = b.ScreenTop;
            if (b.ScreenLeft + b.ScreenWidth > maxRight) maxRight = b.ScreenLeft + b.ScreenWidth;
            if (b.ScreenTop + b.ScreenHeight > maxBottom) maxBottom = b.ScreenTop + b.ScreenHeight;
        }

        return (minLeft, minTop, maxRight, maxBottom);
    }
}
