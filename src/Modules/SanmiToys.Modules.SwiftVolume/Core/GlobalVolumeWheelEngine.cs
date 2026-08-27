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

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    public GlobalVolumeWheelEngine(Func<SwiftVolumeSettings> settingsAccessor, Action<float, bool> onVolumeChanged)
    {
        _settingsAccessor = settingsAccessor;
        _onVolumeChanged = onVolumeChanged;
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
            if (settings.IsEnabled)
            {
                var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsCursorOnTrayArea(hookStruct.pt))
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

    private static bool IsCursorOnTrayArea(POINT pt)
    {
        IntPtr hwnd = WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return false;

        IntPtr curr = hwnd;
        for (int i = 0; i < 6 && curr != IntPtr.Zero; i++)
        {
            var sb = new StringBuilder(256);
            GetClassName(curr, sb, sb.Capacity);
            string cls = sb.ToString();

            // トレイ領域（通知領域）またはオーバーフロートレイ（隠れたインジケーター）
            if (cls is "TrayNotifyWnd" 
                    or "NotifyIconOverflowWindow" 
                    or "TopLevelWindowForOverflowXamlIsland" 
                    or "SysPager")
            {
                return true;
            }

            curr = GetParent(curr);
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
    }
}
