using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SanmiToys.Core.Helpers;
using SanmiToys.Modules.SnapTrans.Services;

namespace SanmiToys.Modules.SnapTrans.Views;

public partial class ResultOverlay : Window
{
    private readonly string _text;
    private readonly TextToSpeechService? _ttsService;
    private bool _isClosing;
    private bool _isPinned;

    private IntPtr _mouseHook = IntPtr.Zero;
    private NativeMethods.LowLevelMouseProc? _mouseProc;

    public ResultOverlay(string text, TextToSpeechService? ttsService = null)
    {
        InitializeComponent();

        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);

        _text = text;
        _ttsService = ttsService;
        ResultTextBox.Text = text;

        this.Loaded += (s, e) =>
        {
            InstallMouseHook();
            try { this.Activate(); } catch { }
        };

        this.Closed += (s, e) =>
        {
            UninstallMouseHook();
        };

        this.KeyDown += (s, e) =>
        {
            if (e.Key == Key.Escape) SafeClose();
        };

        this.MouseLeftButtonDown += (s, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { this.DragMove(); } catch { }
            }
        };

        // ウィンドウ外をクリック（フォーカス喪失）で自動的に閉じる（ピン留め時は維持）
        this.Deactivated += (s, e) =>
        {
            if (_isPinned || _isClosing) return;
            SafeClose();
        };
    }

    public void SetPosition(double screenX, double screenY)
    {
        this.Left = Math.Max(10, screenX);
        this.Top = Math.Max(10, screenY);
    }

    private void SafeClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        UninstallMouseHook();
        try
        {
            Close();
        }
        catch { }
    }

    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;

        _mouseProc = MouseHookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        IntPtr hModule = NativeMethods.GetModuleHandle(curModule?.ModuleName);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hModule, 0);
    }

    private void UninstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            _mouseProc = null;
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_isPinned && !_isClosing)
        {
            int msg = wParam.ToInt32();
            if (msg == NativeMethods.WM_LBUTTONDOWN ||
                msg == NativeMethods.WM_RBUTTONDOWN ||
                msg == NativeMethods.WM_MBUTTONDOWN)
            {
                var hookStruct = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (!ContainsScreenPoint(hookStruct.pt.X, hookStruct.pt.Y))
                {
                    Dispatcher.InvokeAsync(SafeClose);
                }
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public bool ContainsScreenPoint(int screenX, int screenY)
    {
        if (!this.IsVisible) return false;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var pt = new NativeMethods.POINT { X = screenX, Y = screenY };
        var clickedHwnd = NativeMethods.WindowFromPoint(pt);
        if (clickedHwnd != IntPtr.Zero)
        {
            var root = NativeMethods.GetAncestor(clickedHwnd, NativeMethods.GA_ROOT);
            if (root == hwnd || clickedHwnd == hwnd)
            {
                return true;
            }
        }

        if (NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return screenX >= rect.Left && screenX <= rect.Right &&
                   screenY >= rect.Top && screenY <= rect.Bottom;
        }

        return false;
    }

    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        UpdatePinButtonVisual();
    }

    private void UpdatePinButtonVisual()
    {
        if (_isPinned)
        {
            PinButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            PinButton.ToolTip = SanmiToys.Core.Services.LocalizationService.Instance["SnapTrans_ToolTip_Unpin"];
            this.Topmost = true;
        }
        else
        {
            PinButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent;
            PinButton.ToolTip = SanmiToys.Core.Services.LocalizationService.Instance["SnapTrans_ToolTip_Pin"];
        }
    }

    private async void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(_text);
            CopyButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Checkmark24);
            await Task.Delay(1200);
            CopyButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Copy24);
        }
        catch { }
    }

    private void OnSpeakClicked(object sender, RoutedEventArgs e)
    {
        _ttsService?.Speak(_text);
    }
}
