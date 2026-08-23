using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using System.Windows.Threading;
using SanmiToys.Modules.FocusDimmer.Models;

namespace SanmiToys.Modules.FocusDimmer.Core;

public class DimmerEngine : IDisposable
{
    private readonly List<DimmerOverlay> _overlays;
    private readonly Func<FocusDimmerSettings> _settingsAccessor;
    private readonly DispatcherTimer _monitorTimer;

    private bool _isEnabled = true;
    private bool _isPaused = false;
    private IntPtr _lastForegroundWindow = IntPtr.Zero;
    private FocusDimmerNativeMethods.RECT _lastRectForMotion = new();
    private int _highSpeedFrames = 0;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            _isEnabled = value;
            if (!_isEnabled)
            {
                foreach (var ov in _overlays) ov.SetVisibility(false);
            }
        }
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => _isPaused = value;
    }

    public DimmerEngine(List<DimmerOverlay> overlays, Func<FocusDimmerSettings> settingsAccessor)
    {
        _overlays = overlays;
        _settingsAccessor = settingsAccessor;

        _monitorTimer = new DispatcherTimer();
        _monitorTimer.Interval = TimeSpan.FromMilliseconds(100);
        _monitorTimer.Tick += MonitorTimer_Tick;
    }

    public void Start() => _monitorTimer.Start();
    public void Stop()
    {
        _monitorTimer.Stop();
        foreach (var ov in _overlays) ov.SetVisibility(false);
    }

    private void MonitorTimer_Tick(object? sender, EventArgs? e)
    {
        if (!_isEnabled)
        {
            foreach (var ov in _overlays) ov.SetVisibility(false);
            return;
        }

        if (_isPaused)
        {
            // インスペクターモード中は現在の減光・くり抜き状態をそのまま完全に維持（フリーズ）
            return;
        }

        try
        {
            foreach (var overlay in _overlays)
            {
                overlay.EnsureTopmost();
            }

            uint idleMs = GetIdleTimeMs();
            double idleSec = idleMs / 1000.0;

            IntPtr foregroundWindow = FocusDimmerNativeMethods.GetForegroundWindow();

            if (foregroundWindow != IntPtr.Zero && (!FocusDimmerNativeMethods.IsWindowVisible(foregroundWindow) || FocusDimmerNativeMethods.IsIconic(foregroundWindow)))
            {
                foregroundWindow = IntPtr.Zero;
            }

            if (IsIgnoredWindow(foregroundWindow))
            {
                foregroundWindow = _lastForegroundWindow;
            }

            bool globalWindowChanged = (foregroundWindow != _lastForegroundWindow);
            if (globalWindowChanged)
            {
                _lastForegroundWindow = foregroundWindow;
            }

            FocusDimmerNativeMethods.RECT currentRect = new();
            bool isMoving = false;

            if (foregroundWindow != IntPtr.Zero)
            {
                if (globalWindowChanged)
                {
                    // ウィンドウ切り替え時は移動ではないため、タイト枠で一発確定してチラつきを防止
                    if (!FocusDimmerNativeMethods.GetTightWindowRect(foregroundWindow, out currentRect))
                    {
                        FocusDimmerNativeMethods.GetWindowRect(foregroundWindow, out currentRect);
                    }
                    // 動作検出は GetWindowRect 同士で比較する。タイト枠を保存すると、
                    // 新規表示直後に影領域の差だけで「ドラッグ中」と誤認してしまう。
                    if (!FocusDimmerNativeMethods.GetWindowRect(foregroundWindow, out _lastRectForMotion))
                    {
                        _lastRectForMotion = currentRect;
                    }
                    isMoving = false;
                    _highSpeedFrames = 0;
                }
                else
                {
                    // 同一ウィンドウでの座標変化（ドラッグ移動）の検出
                    FocusDimmerNativeMethods.RECT rawRect;
                    if (FocusDimmerNativeMethods.GetWindowRect(foregroundWindow, out rawRect))
                    {
                        if (!rawRect.Equals(_lastRectForMotion))
                        {
                            isMoving = true;
                            _lastRectForMotion = rawRect;
                            currentRect = rawRect;
                            // ドラッグ中の穴あけは 30fps に限定し、追従性と合成負荷を両立する。
                            _monitorTimer.Interval = TimeSpan.FromMilliseconds(33);
                            _highSpeedFrames = 12;
                        }
                        else
                        {
                            if (_highSpeedFrames > 0)
                            {
                                _highSpeedFrames--;
                                currentRect = rawRect;
                            }
                            else
                            {
                                if (_monitorTimer.Interval.TotalMilliseconds < 120)
                                {
                                    _monitorTimer.Interval = TimeSpan.FromMilliseconds(120);
                                }
                                // 静止時はタイト枠で隙間なく吸い付き
                                if (!FocusDimmerNativeMethods.GetTightWindowRect(foregroundWindow, out currentRect))
                                {
                                    currentRect = rawRect;
                                }
                            }
                        }
                    }
                }
            }

            // Snipping Tool / 画面キャプチャツールの判定（フォアグラウンドまたは切り取りウィンドウの存在）
            bool isCaptureTool = IsScreenCaptureActive(foregroundWindow);

            bool isDesktopOrNull = (foregroundWindow == IntPtr.Zero) || IsDesktopWindow(foregroundWindow);

            string activeDeviceName = "";
            if (!isDesktopOrNull && foregroundWindow != IntPtr.Zero)
            {
                var center = new Point((currentRect.Left + currentRect.Right) / 2, (currentRect.Top + currentRect.Bottom) / 2);
                var activeScreen = Screen.FromPoint(center);
                if (activeScreen != null) activeDeviceName = activeScreen.DeviceName;
            }

            var settings = _settingsAccessor();
            bool areMonitorsLinked = settings.AreMonitorsLinked;

            foreach (var overlay in _overlays)
            {
                if (isCaptureTool)
                {
                    // Snipping Tool 等のキャプチャツール起動中はオーバーレイを完全に非表示にして撮影・選択を邪魔しない
                    overlay.SetVisibility(false);
                    continue;
                }

                if (overlay.LinkedProfile.Opacity <= 0 && !overlay.LinkedProfile.DimWhenIdle)
                {
                    overlay.SetVisibility(false);
                    continue;
                }

                overlay.SetVisibility(true);
                bool isActiveMonitor = (overlay.LinkedProfile.DeviceName == activeDeviceName);
                bool isIdle = overlay.LinkedProfile.DimWhenIdle && (idleSec > (overlay.LinkedProfile.IdleTimeout * 60));

                // モニター連動ON時は全モニターを1枚の仮想モニターとみなす
                bool dimEntirelyInactive = !areMonitorsLinked && !isActiveMonitor && !isDesktopOrNull && overlay.LinkedProfile.DimEntirelyWhenInactive;
                bool forceFullDim = isIdle || dimEntirelyInactive;

                if (forceFullDim)
                {
                    overlay.UpdateState(IntPtr.Zero, true, false, true, isIdle, isMoving);
                }
                else if (isDesktopOrNull)
                {
                    overlay.UpdateState(IntPtr.Zero, false, globalWindowChanged, false, false, isMoving);
                }
                else
                {
                    bool isExcluded = CheckIfExcluded(foregroundWindow, overlay.LinkedProfile, settings);
                    // 連動ON時は全モニターで連動減光（デスクトップのみ減光も全モニターで適用）、連動OFF時はアクティブモニターのみ減光
                    bool shouldDim = (areMonitorsLinked || isActiveMonitor) && !isExcluded;
                    overlay.UpdateState(foregroundWindow, shouldDim, globalWindowChanged, false, false, isMoving);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DimmerEngine] Tick error: {ex.Message}");
        }
    }

    private static bool IsScreenCaptureActive(IntPtr foregroundHwnd)
    {
        if (IsScreenCaptureTool(foregroundHwnd)) return true;

        IntPtr clipHwnd = FocusDimmerNativeMethods.FindWindow("ScreenClippingWindow", null);
        if (clipHwnd != IntPtr.Zero && FocusDimmerNativeMethods.IsWindowVisible(clipHwnd)) return true;

        IntPtr snippingHwnd = FocusDimmerNativeMethods.FindWindow(null, "Screen Snipping");
        if (snippingHwnd != IntPtr.Zero && FocusDimmerNativeMethods.IsWindowVisible(snippingHwnd)) return true;

        return false;
    }

    private static bool IsScreenCaptureTool(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        FocusDimmerNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string procName = ProcessInfoHelper.GetProcessName(pid);

        if (string.IsNullOrEmpty(procName)) return false;

        return procName is "screenclippinghost" or "snippingtool" or "snippingtoolapp" or "screensketch" or "lightshot" or "sharex" or "flameshot" or "greenshot";
    }

    private static uint GetIdleTimeMs()
    {
        var lii = new FocusDimmerNativeMethods.LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(lii);
        lii.dwTime = 0;
        if (FocusDimmerNativeMethods.GetLastInputInfo(ref lii))
        {
            return (uint)Environment.TickCount - lii.dwTime;
        }
        return 0;
    }

    private static bool IsIgnoredWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (WindowHelper.IsMenuOrPopupEx(hwnd)) return false;

        StringBuilder sb = new(256);
        FocusDimmerNativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        string className = sb.ToString();

        if (className is "CiceroUIWndFrame" or "InputIndicator" or "MagUIClass") return true;
        if (className.Contains("SnapLayout")) return true;

        FocusDimmerNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string procName = ProcessInfoHelper.GetProcessName(pid);

        if (procName == "explorer")
        {
            bool isFolder = (className is "CabinetWClass" or "ExploreWClass");
            bool isDesktop = (className is "Progman" or "WorkerW");
            bool isDialog = (className == "#32770");
            if (!isFolder && !isDesktop && !isDialog) return true;
        }

        return procName is "shellexperiencehost" or "startmenuexperiencehost" or "searchhost";
    }

    private static bool IsDesktopWindow(IntPtr hwnd)
    {
        StringBuilder sb = new(256);
        FocusDimmerNativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        string className = sb.ToString();
        return className is "Progman" or "WorkerW";
    }

    private static bool CheckIfExcluded(IntPtr hwnd, MonitorProfile profile, FocusDimmerSettings settings)
    {
        if (hwnd == IntPtr.Zero) return true;
        FocusDimmerNativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        string procName = ProcessInfoHelper.GetProcessName(pid);

        var list = (profile.IgnoreList + "," + settings.IgnoreList).Split(',')
            .Select(x => x.Trim().ToLowerInvariant().Replace(".exe", ""))
            .Where(x => !string.IsNullOrEmpty(x));

        if (list.Contains(procName)) return true;

        if (profile.ExcludeTopmost)
        {
            int exStyle = FocusDimmerNativeMethods.GetWindowLong(hwnd, FocusDimmerNativeMethods.GWL_EXSTYLE);
            if ((exStyle & FocusDimmerNativeMethods.WS_EX_TOPMOST) != 0) return true;
        }

        var placement = new FocusDimmerNativeMethods.WINDOWPLACEMENT();
        placement.length = Marshal.SizeOf(placement);
        if (FocusDimmerNativeMethods.GetWindowPlacement(hwnd, ref placement))
        {
            if (placement.showCmd == 3) return true; // SW_SHOWMAXIMIZED
        }

        return false;
    }

    public void Dispose()
    {
        _monitorTimer.Stop();
    }
}
