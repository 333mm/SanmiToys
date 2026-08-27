using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SanmiToys.Core.Helpers;
using SanmiToys.Core.Interfaces;
using SanmiToys.Core.Services;
using SanmiToys.Modules.SnapTrans.Models;
using SanmiToys.Modules.SnapTrans.Services;
using SanmiToys.Modules.SnapTrans.Views;

namespace SanmiToys.Modules.SnapTrans;

public class SnapTransModule : IToyModule
{
    private const int HOTKEY_ID = 0x5354; // 'ST' (SnapTrans)
    private readonly SettingsService _settingsService;
    private SnapTransSettings _settings = new();
    private readonly OcrService _ocrService = new();
    private readonly TranslationService _translationService = new();
    private readonly TextToSpeechService _ttsService = new();

    private HwndSource? _hwndSource;
    private bool _isHotkeyRegistered = false;

    public string Id => "SnapTrans";
    public string Name => "SnapTrans";
    public string Description => LocalizationService.Instance["SnapTrans_Desc"];
    public string IconGlyph => "\uE8C1"; // Translate icon

    public bool IsEnabled
    {
        get => _settings.IsEnabled;
        set
        {
            if (_settings.IsEnabled != value)
            {
                _settings.IsEnabled = value;
                _settingsService.SetModuleSettings(Id, _settings);
                _settingsService.SetModuleEnabled(Id, value);

                if (value)
                {
                    UpdateHotkeyRegistration();
                }
                else
                {
                    UnregisterHotkey();
                }
            }
        }
    }

    public SnapTransModule(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = _settingsService.GetModuleSettings<SnapTransSettings>(Id);
        _settings.IsEnabled = _settingsService.IsModuleEnabled(Id, false);
    }

    public Task InitializeAsync()
    {
        if (_settings.IsEnabled)
        {
            Start();
        }

        return Task.CompletedTask;
    }

    public void Start()
    {
        EnsureMessageWindow();
        if (IsEnabled)
        {
            UpdateHotkeyRegistration();
        }
    }

    public void Stop()
    {
        UnregisterHotkey();
        _ttsService.Stop();
        if (_hwndSource != null)
        {
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }

    private static void RunOnUi(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return;
        if (app.Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            app.Dispatcher.InvokeAsync(action);
        }
    }

    private void EnsureMessageWindow()
    {
        if (_hwndSource == null)
        {
            RunOnUi(() =>
            {
                var parameters = new HwndSourceParameters("SnapTransHotkeyMessageSink")
                {
                    Width = 0,
                    Height = 0,
                    PositionX = 0,
                    PositionY = 0,
                    WindowStyle = 0x800000 // WS_BORDER invisible
                };
                _hwndSource = new HwndSource(parameters);
                _hwndSource.AddHook(HwndHook);
            });
        }
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            TriggerSnipping();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void UpdateHotkeyRegistration()
    {
        EnsureMessageWindow();
        if (_hwndSource == null) return;

        UnregisterHotkey();

        if (!IsEnabled) return;

        uint modifiers = NativeMethods.MOD_NOREPEAT;
        if (_settings.HotkeyCtrl) modifiers |= NativeMethods.MOD_CONTROL;
        if (_settings.HotkeyAlt) modifiers |= NativeMethods.MOD_ALT;
        if (_settings.HotkeyShift) modifiers |= NativeMethods.MOD_SHIFT;
        if (_settings.HotkeyWin) modifiers |= NativeMethods.MOD_WIN;

        if (Enum.TryParse<Key>(_settings.HotkeyKey, true, out var key) && key != Key.None)
        {
            uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            if (vk != 0)
            {
                _isHotkeyRegistered = NativeMethods.RegisterHotKey(_hwndSource.Handle, HOTKEY_ID, modifiers, vk);
            }
        }
    }

    private void UnregisterHotkey()
    {
        if (_isHotkeyRegistered && _hwndSource != null)
        {
            NativeMethods.UnregisterHotKey(_hwndSource.Handle, HOTKEY_ID);
            _isHotkeyRegistered = false;
        }
    }

    public void TriggerSnipping()
    {
        if (!IsEnabled) return;

        // プチフリーズ防止: バックグラウンドスレッドで画面キャプチャを非同期取得
        Task.Run(() =>
        {
            var virtualBounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            var capturedFullBitmap = new System.Drawing.Bitmap(virtualBounds.Width, virtualBounds.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(capturedFullBitmap))
            {
                g.CopyFromScreen(virtualBounds.Left, virtualBounds.Top, 0, 0, virtualBounds.Size, System.Drawing.CopyPixelOperation.SourceCopy);
            }

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                var win = new SnippingWindow(capturedFullBitmap, virtualBounds, _settings, _ocrService, _translationService, _ttsService);
                win.Show();
            });
        });
    }

    public object? CreateSettingsView()
    {
        return new SnapTransSettingsView(this, _settingsService, _settings);
    }
}
