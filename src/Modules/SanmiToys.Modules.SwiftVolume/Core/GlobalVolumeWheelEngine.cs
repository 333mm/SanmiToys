using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SanmiToys.Modules.SwiftVolume.Models;

namespace SanmiToys.Modules.SwiftVolume.Core;

/// <summary>
/// タスクトレイ（通知領域）アイコン上でのみ、マウスホイールによる 1% 単位の音量調整を行う低レベルフックエンジン。
/// ※通常のタスクバー領域（タスクバーボタンや空白）では動作しません。
/// </summary>
public class GlobalVolumeWheelEngine : IDisposable
{
    private readonly Func<SwiftVolumeSettings> _settingsAccessor;
    private readonly Action<float, bool> _onVolumeChanged;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelMouseProc? _proc;

    public const int WH_MOUSE_LL = 14;
    public const int WM_MOUSEWHEEL = 0x020A;

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private readonly Func<POINT, bool>? _isCursorOnSpeakerIcon;

    public GlobalVolumeWheelEngine(Func<SwiftVolumeSettings> settingsAccessor, Action<float, bool> onVolumeChanged, Func<POINT, bool>? isCursorOnSpeakerIcon = null)
    {
        _settingsAccessor = settingsAccessor;
        _onVolumeChanged = onVolumeChanged;
        _isCursorOnSpeakerIcon = isCursorOnSpeakerIcon;
    }

    public void Start()
    {
        if (_hookId != IntPtr.Zero) return;
        _proc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var hModule = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
        _hookId = SetWindowsHookEx(WH_MOUSE_LL, _proc, hModule, 0);
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            _proc = null;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == WM_MOUSEWHEEL)
        {
            var settings = _settingsAccessor();
            if (settings.IsEnabled && settings.EnableTaskbarVolumeWheel)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                bool matches = settings.TaskbarWheelTrayIconOnly
                    ? (_isCursorOnSpeakerIcon != null ? _isCursorOnSpeakerIcon(hookStruct.pt) : IsCursorOnTrayOnlyArea(hookStruct.pt))
                    : IsCursorOnTaskbarArea(hookStruct.pt);

                if (matches)
                {
                    short wheelDelta = (short)((hookStruct.mouseData >> 16) & 0xFFFF);
                    float delta = wheelDelta > 0 ? 1.0f : -1.0f; // 1% ずつ調整

                    // 低レベルフック内での同期的COM/UI呼び出しによるフリーズ・ハングを防止するため、
                    // スレッドプール上で非同期に実行
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            float newVol = AudioDeviceHelper.StepVolume(delta);
                            bool isMuted = AudioDeviceHelper.GetIsMuted();
                            _onVolumeChanged(newVol, isMuted);
                        }
                        catch { }
                    });

                    return new IntPtr(1); // トレイ上のスクロールイベントを消費
                }
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// タスクバー全域（タスクバーボタン、空白領域、通知領域）またはトレイオーバーフロー上にあるかを判定
    /// </summary>
    private static bool IsCursorOnTaskbarArea(POINT pt)
    {
        IntPtr hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;

        IntPtr curr = hwnd;
        for (int i = 0; i < 8 && curr != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(256);
            GetClassName(curr, sb, sb.Capacity);
            string cls = sb.ToString();

            // トレイ領域（通知領域）、タスクバー、オーバーフロートレイ（Windows 10 / 11 対応）
            if (cls is "TrayNotifyWnd" 
                    or "NotifyIconOverflowWindow" 
                    or "TopLevelWindowForOverflowXamlIsland" 
                    or "SysPager"
                    or "ToolbarWindow32"
                    or "Shell_TrayWnd"
                    or "Shell_SecondaryTrayWnd"
                    or "Windows.UI.Input.InputSite.WindowClass"
                    or "Windows.UI.Composition.DesktopWindowTarget")
            {
                return true;
            }

            curr = GetParent(curr);
        }

        return false;
    }

    /// <summary>
    /// タスクトレイ（通知領域）またはトレイオーバーフロー上「のみ」にあるかを判定
    /// （通常のタスクバーアプリボタンやタスクバー余白領域は除外）
    /// </summary>
    private static bool IsCursorOnTrayOnlyArea(POINT pt)
    {
        IntPtr hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;

        // 1. オーバーフロートレイ（隠れたインジケーター）ウィンドウのチェック
        IntPtr curr = hwnd;
        for (int i = 0; i < 8 && curr != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(256);
            GetClassName(curr, sb, sb.Capacity);
            string cls = sb.ToString();

            if (cls is "NotifyIconOverflowWindow" 
                    or "TopLevelWindowForOverflowXamlIsland")
            {
                return true;
            }

            // 直接 TrayNotifyWnd またはその子孫 (SysPager, ToolbarWindow32) にヒットした場合 (Win10等)
            if (cls is "TrayNotifyWnd" or "SysPager")
            {
                return true;
            }

            curr = GetParent(curr);
        }

        // 2. メインタスクバー上の TrayNotifyWnd 矩形判定 (Win11 / Win10 共通)
        IntPtr hTray = FindWindow("Shell_TrayWnd", null);
        if (hTray != IntPtr.Zero)
        {
            IntPtr hNotify = FindWindowEx(hTray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (hNotify != IntPtr.Zero && GetWindowRect(hNotify, out RECT rcNotify))
            {
                if (pt.x >= rcNotify.Left && pt.x <= rcNotify.Right &&
                    pt.y >= rcNotify.Top && pt.y <= rcNotify.Bottom)
                {
                    return true;
                }
            }
        }

        // 3. セカンダリタスクバー上の TrayNotifyWnd 矩形判定 (マルチモニター)
        IntPtr hSecTray = FindWindow("Shell_SecondaryTrayWnd", null);
        while (hSecTray != IntPtr.Zero)
        {
            IntPtr hSecNotify = FindWindowEx(hSecTray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (hSecNotify != IntPtr.Zero && GetWindowRect(hSecNotify, out RECT rcSecNotify))
            {
                if (pt.x >= rcSecNotify.Left && pt.x <= rcSecNotify.Right &&
                    pt.y >= rcSecNotify.Top && pt.y <= rcSecNotify.Bottom)
                {
                    return true;
                }
            }
            hSecTray = FindWindowEx(IntPtr.Zero, hSecTray, "Shell_SecondaryTrayWnd", null);
        }

        // 4. Windows 11 の XAML アイランド内部で TrayNotifyWnd が隠蔽されている場合の高精度フォールバック:
        // カーソルがタスクバー上にあり、かつ通知領域側（通常の横タスクバーなら右端からトレイ幅の範囲）にあるか判定
        if (IsCursorOnTaskbarArea(pt))
        {
            IntPtr targetTray = hTray != IntPtr.Zero ? hTray : FindWindow("Shell_TrayWnd", null);
            if (targetTray != IntPtr.Zero && GetWindowRect(targetTray, out RECT rcTb))
            {
                int tbWidth = rcTb.Right - rcTb.Left;
                int tbHeight = rcTb.Bottom - rcTb.Top;
                if (tbWidth > tbHeight)
                {
                    // 横向きタスクバー（標準の下部または上部タスクバー）:
                    // 右端から時計＋トレイ領域（最大450px、最小200px、またはタスクバー幅の30%）をトレイ領域と判定
                    int trayWidth = Math.Clamp(tbWidth / 3, 200, 450);
                    if (pt.x >= rcTb.Right - trayWidth && pt.x <= rcTb.Right &&
                        pt.y >= rcTb.Top && pt.y <= rcTb.Bottom)
                    {
                        return true;
                    }
                }
                else
                {
                    // 縦向きタスクバー（左または右配置）の場合、下部をトレイ領域と判定
                    int trayHeight = Math.Clamp(tbHeight / 3, 150, 400);
                    if (pt.y >= rcTb.Bottom - trayHeight && pt.y <= rcTb.Bottom &&
                        pt.x >= rcTb.Left && pt.x <= rcTb.Right)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}
