using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SanmiToys.Core;
using SanmiToys.Core.Interfaces;
using SanmiToys.Core.Services;
using SanmiToys.Modules.FluidDrag;
using SanmiToys.Modules.FocusDimmer;
using SanmiToys.Modules.SnapTrans;
using SanmiToys.Modules.SwiftVolume;
using Velopack;
using Wpf.Ui.Appearance;

namespace SanmiToys.Host;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private static bool _hasMutexOwnership = false;
    private readonly List<IToyModule> _modules = new();
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {

        const string mutexName = @"Local\SanmiToys_SingleInstance_Mutex";
        try
        {
            _mutex = new Mutex(true, mutexName, out bool createdNew);
            _hasMutexOwnership = createdNew;
        }
        catch
        {
            _hasMutexOwnership = true; // 例外時は安全のため起動を継続
        }

#if !DEBUG
        if (!_hasMutexOwnership)
        {
            Shutdown();
            return;
        }
#endif

        // アプリ全体のグローバル例外ハンドラー（エラーコード表示＆コピー対応＆ログファイル出力）
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                AppLogger.Error("Host", "Unhandled exception in AppDomain", ex);
                ErrorDialogService.ShowError("未処理の例外が発生しました (AppDomain)", ex.Message, ex);
            }
        };

        this.DispatcherUnhandledException += (s, args) =>
        {
            AppLogger.Error("Host", "Unhandled exception in Dispatcher", args.Exception);
            ErrorDialogService.ShowError("UIスレッドで例外が発生しました (Dispatcher)", args.Exception.Message, args.Exception);
            args.Handled = true; // アプリの強制終了を防止
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            AppLogger.Error("Host", "Unobserved task exception in TaskScheduler", args.Exception);
            ErrorDialogService.ShowError("非同期タスクで例外が発生しました (TaskScheduler)", args.Exception.Message, args.Exception);
            args.SetObserved();
        };

        AppLogger.Info("Host", "SanmiToys starting up...");

        // アプリ全体の ScrollViewer に対するマウスホイールスクロールの確実なグローバルハンドリング
        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnGlobalScrollViewerPreviewMouseWheel),
            true);

        base.OnStartup(e);

        FreezeWatchdogService.Start(this.Dispatcher);

        ApplicationThemeManager.ApplySystemTheme();
        InitSystemAccentColorMonitoring();

        var settingsService = SettingsService.Instance;

        // モジュールのインスタンス化と登録
        var fluidDrag = new FluidDragModule(settingsService);
        var focusDimmer = new FocusDimmerModule(settingsService);
        var snapTrans = new SnapTransModule(settingsService);
        var swiftVolume = new SwiftVolumeModule(settingsService, modId =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _mainWindow?.ShowWindow();
                _mainWindow?.NavigateToModule(modId);
            });
        });

        _modules.Add(fluidDrag);
        _modules.Add(focusDimmer);
        _modules.Add(snapTrans);
        _modules.Add(swiftVolume);

        // 先に MainWindow を生成・表示して UI のハング・遅延を根絶
        _mainWindow = new MainWindow(_modules);

        bool startMinimized = e.Args.Length > 0 && e.Args[0] == "--minimized";
        if (!startMinimized)
        {
            _mainWindow.ShowWindow();
        }

        // 各モジュールの初期化と起動を設定に従ってUIスレッド上で確実に実行
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                foreach (var module in _modules)
                {
                    try
                    {
                        await module.InitializeAsync();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Host", $"Module initialization error: {module.Name}", ex);
                    }
                }

                _mainWindow?.RefreshDashboardState();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Host", "Startup module runner error", ex);
            }
        }, System.Windows.Threading.DispatcherPriority.Normal);
    }

    private static void OnGlobalScrollViewerPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv && !e.Handled)
        {
            // 水平スクロール（Shiftキー押下時、または水平スクロールバーのみの場合）
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || (sv.ScrollableHeight == 0 && sv.ScrollableWidth > 0))
            {
                if (e.Delta < 0)
                {
                    sv.ScrollToHorizontalOffset(sv.HorizontalOffset + 48);
                }
                else
                {
                    sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset - 48));
                }
                e.Handled = true;
            }
            else if (sv.ScrollableHeight > 0)
            {
                // 縦スクロール
                double scrollDelta = e.Delta > 0 ? -48 : 48;
                double newOffset = Math.Clamp(sv.VerticalOffset + scrollDelta, 0, sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(newOffset);
                e.Handled = true;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FreezeWatchdogService.Stop();
        AppLogger.Info("Host", "SanmiToys shutting down cleanly.");

        try
        {
            if (_hasMutexOwnership && _mutex != null)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // 他のスレッドやプロセス状況による解放エラーを安全に無視
        }
        finally
        {
            try
            {
                _mutex?.Dispose();
                _mutex = null;
            }
            catch { }
        }

        base.OnExit(e);
    }

    private static global::Windows.UI.ViewManagement.UISettings? _uiSettings;

    private static void InitSystemAccentColorMonitoring()
    {
        try
        {
            _uiSettings = new global::Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (sender, args) =>
            {
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    ApplyWindowsSystemAccentColor();
                });
            };
        }
        catch { }

        ApplyWindowsSystemAccentColor();
    }

    private static void ApplyWindowsSystemAccentColor()
    {
        try
        {
            var accentColor = GetRealWindowsAccentColor();

            // 1. WPF-UI の AccentColorManager に適用
            try
            {
                ApplicationAccentColorManager.Apply(accentColor);
            }
            catch { }

            // 2. Application.Current.Resources に直接各アクセントブラシを確実に注入・更新
            if (System.Windows.Application.Current != null)
            {
                var defaultBrush = new System.Windows.Media.SolidColorBrush(accentColor);
                defaultBrush.Freeze();

                var secColor = System.Windows.Media.Color.FromArgb(230, accentColor.R, accentColor.G, accentColor.B);
                var secBrush = new System.Windows.Media.SolidColorBrush(secColor);
                secBrush.Freeze();

                var tertColor = System.Windows.Media.Color.FromArgb(180, accentColor.R, accentColor.G, accentColor.B);
                var tertBrush = new System.Windows.Media.SolidColorBrush(tertColor);
                tertBrush.Freeze();

                System.Windows.Application.Current.Resources["AccentFillColorDefaultBrush"] = defaultBrush;
                System.Windows.Application.Current.Resources["AccentFillColorSecondaryBrush"] = secBrush;
                System.Windows.Application.Current.Resources["AccentFillColorTertiaryBrush"] = tertBrush;
                System.Windows.Application.Current.Resources["AccentTextFillColorPrimaryBrush"] = defaultBrush;
                System.Windows.Application.Current.Resources["SystemAccentColor"] = accentColor;
                System.Windows.Application.Current.Resources["SystemAccentBrush"] = defaultBrush;
            }
        }
        catch { }
    }

    private static System.Windows.Media.Color GetRealWindowsAccentColor()
    {
        System.Windows.Media.Color baseColor = System.Windows.Media.Color.FromRgb(0, 120, 215);
        bool found = false;

        // 1. WinRT UISettings から取得（ダークテーマ向けの AccentLight2 / AccentLight1 を優先）
        try
        {
            if (_uiSettings != null)
            {
                var lightColor = _uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentLight2);
                if (lightColor.A > 0 && (lightColor.R > 0 || lightColor.G > 0 || lightColor.B > 0))
                {
                    return System.Windows.Media.Color.FromArgb(lightColor.A, lightColor.R, lightColor.G, lightColor.B);
                }

                var midColor = _uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.AccentLight1);
                if (midColor.A > 0 && (midColor.R > 0 || midColor.G > 0 || midColor.B > 0))
                {
                    return System.Windows.Media.Color.FromArgb(midColor.A, midColor.R, midColor.G, midColor.B);
                }

                var winColor = _uiSettings.GetColorValue(global::Windows.UI.ViewManagement.UIColorType.Accent);
                if (winColor.A > 0 && (winColor.R > 0 || winColor.G > 0 || winColor.B > 0))
                {
                    baseColor = System.Windows.Media.Color.FromArgb(winColor.A, winColor.R, winColor.G, winColor.B);
                    found = true;
                }
            }
        }
        catch { }

        // 2. レジストリ DWM から取得
        if (!found)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
                if (key != null)
                {
                    var val = key.GetValue("AccentColor");
                    if (val is int intVal)
                    {
                        byte b = (byte)((intVal >> 16) & 0xFF);
                        byte g = (byte)((intVal >> 8) & 0xFF);
                        byte r = (byte)(intVal & 0xFF);
                        baseColor = System.Windows.Media.Color.FromRgb(r, g, b);
                        found = true;
                    }
                    else
                    {
                        var colVal = key.GetValue("ColorizationColor");
                        if (colVal is int cVal)
                        {
                            byte r = (byte)((cVal >> 16) & 0xFF);
                            byte g = (byte)((cVal >> 8) & 0xFF);
                            byte b = (byte)(cVal & 0xFF);
                            baseColor = System.Windows.Media.Color.FromRgb(r, g, b);
                            found = true;
                        }
                    }
                }
            }
            catch { }
        }

        // 3. SystemParameters から取得
        if (!found)
        {
            try
            {
                var wc = SystemParameters.WindowGlassColor;
                baseColor = System.Windows.Media.Color.FromRgb(wc.R, wc.G, wc.B);
            }
            catch { }
        }

        // ダークテーマ上で美しく映えるように明度と彩度を最適化
        return AdjustColorForDarkTheme(baseColor);
    }

    private static System.Windows.Media.Color AdjustColorForDarkTheme(System.Windows.Media.Color color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        if (delta > 0)
        {
            if (max == r) h = 60.0 * (((g - b) / delta) % 6.0);
            else if (max == g) h = 60.0 * (((b - r) / delta) + 2.0);
            else h = 60.0 * (((r - g) / delta) + 4.0);
            if (h < 0) h += 360.0;
        }

        double s = max == 0 ? 0 : delta / max;
        double v = max;

        // ダーク背景上で暗く沈んで濃く見えないよう、明度(V)を引き上げ、彩度(S)を上品に調整
        v = Math.Clamp(v * 1.30 + 0.20, 0.82, 1.0);
        s = Math.Clamp(s * 0.85, 0.35, 0.88);

        int hi = (int)(Math.Floor(h / 60.0)) % 6;
        double f = (h / 60.0) - Math.Floor(h / 60.0);

        double p = v * (1.0 - s);
        double q = v * (1.0 - (f * s));
        double t = v * (1.0 - ((1.0 - f) * s));

        double nr, ng, nb;
        switch (hi)
        {
            case 0: nr = v; ng = t; nb = p; break;
            case 1: nr = q; ng = v; nb = p; break;
            case 2: nr = p; ng = v; nb = t; break;
            case 3: nr = p; ng = q; nb = v; break;
            case 4: nr = t; ng = p; nb = v; break;
            default: nr = v; ng = p; nb = q; break;
        }

        byte br = (byte)Math.Clamp((int)Math.Round(nr * 255.0), 0, 255);
        byte bg = (byte)Math.Clamp((int)Math.Round(ng * 255.0), 0, 255);
        byte bb = (byte)Math.Clamp((int)Math.Round(nb * 255.0), 0, 255);

        return System.Windows.Media.Color.FromRgb(br, bg, bb);
    }
}
