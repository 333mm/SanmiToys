using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SanmiToys.Modules.SwiftVolume.Core;

/// <summary>
/// Win32 Shell_NotifyIcon を直接ラップするシンプルなトレイアイコン実装。
/// H.NotifyIcon の WPF DependencyProperty 経由ではなく Win32 API を直接呼び出すため、
/// アイコン更新が確実に反映される。
/// </summary>
internal sealed class DirectTrayIcon : IDisposable
{
    #region Win32

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 1;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int uMsg, IntPtr wParam, IntPtr lParam);

    #endregion

    private static uint _nextId = 2000;
    private readonly uint _id;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _added = false;
    private Icon? _currentIcon;
    private string _currentTip = "SwiftVolume";

    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action? MiddleClick;

    public DirectTrayIcon()
    {
        _id = System.Threading.Interlocked.Increment(ref _nextId);
    }

    /// <summary>メッセージウィンドウ HWND を設定して Shell_NotifyIcon NIM_ADD を実行する。</summary>
    public void Create(IntPtr messageWindowHwnd)
    {
        _hwnd = messageWindowHwnd;
        var data = BuildData(NIF_MESSAGE);
        Shell_NotifyIcon(NIM_ADD, ref data);
        _added = true;
    }

    /// <summary>アイコンを更新する。毎回 NIM_MODIFY を呼ぶため確実に反映される。</summary>
    public void UpdateIcon(Icon icon, string tooltip)
    {
        if (!_added || _hwnd == IntPtr.Zero) return;

        _currentTip = tooltip;

        // 古い Icon を Dispose するために保存
        var oldIcon = _currentIcon;
        _currentIcon = icon;

        var data = BuildData(NIF_ICON | NIF_TIP);
        data.hIcon = icon.Handle;
        data.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;

        Shell_NotifyIcon(NIM_MODIFY, ref data);

        // 古い Icon を Dispose（ただし現在セット中のハンドルを破棄しないよう注意）
        if (!ReferenceEquals(oldIcon, icon))
            oldIcon?.Dispose();
    }

    /// <summary>WM_TRAYICON メッセージをこのオブジェクトに転送して処理する。</summary>
    public bool ProcessMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_TRAYICON || (uint)wParam.ToInt32() != _id)
            return false;

        int notification = lParam.ToInt32() & 0xFFFF;
        switch (notification)
        {
            case 0x0201: // WM_LBUTTONUP
            case 0x0205: // WM_RBUTTONUP
                if (notification == 0x0201) LeftClick?.Invoke();
                else RightClick?.Invoke();
                break;
            case 0x0207: // WM_MBUTTONUP
                MiddleClick?.Invoke();
                break;
        }
        return true;
    }

    public uint CallbackMessage => (uint)WM_TRAYICON;
    public uint IconId => _id;

    private NOTIFYICONDATA BuildData(uint flags)
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = _id,
            uFlags = flags | NIF_MESSAGE,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _currentIcon?.Handle ?? IntPtr.Zero,
            szTip = _currentTip?.Length > 127 ? _currentTip[..127] : (_currentTip ?? "")
        };
    }

    public void Dispose()
    {
        if (_added && _hwnd != IntPtr.Zero)
        {
            var data = BuildData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
