using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Win32;

namespace SanmiToys.Core.Services;

public static class FreezeWatchdogService
{
    private static Thread? _watchdogThread;
    private static Dispatcher? _targetDispatcher;
    private static volatile bool _isRunning;
    private static long _lastPongTicks;
    private static long _lastPingSentTicks;
    private static int _isPingPending;
    private static bool _freezeReported = false;
    private static bool _systemEventsSubscribed = false;
    private static volatile bool _isSuspended = false;
    private static volatile bool _isSessionLocked = false;
    private static long _graceUntilTicks = 0;

    private const int TIMEOUT_SECONDS = 25;
    private const int RESUME_GRACE_SECONDS = 30;

    public static void Start(Dispatcher dispatcher)
    {
        if (_isRunning) return;
        _targetDispatcher = dispatcher;
        _isRunning = true;
        _lastPongTicks = Environment.TickCount64;
        _lastPingSentTicks = Environment.TickCount64;
        _isPingPending = 0;
        _freezeReported = false;
        _isSuspended = false;
        _isSessionLocked = false;
        _graceUntilTicks = Environment.TickCount64 + (RESUME_GRACE_SECONDS * 1000);

        SubscribeSystemEvents();

        _watchdogThread = new Thread(WatchdogLoop)
        {
            IsBackground = true,
            Name = "SanmiToys_FreezeWatchdog"
        };
        _watchdogThread.Start();
    }

    public static void Stop()
    {
        _isRunning = false;
        UnsubscribeSystemEvents();
    }

    private static void SubscribeSystemEvents()
    {
        if (_systemEventsSubscribed) return;
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _systemEventsSubscribed = true;
        }
        catch { }
    }

    private static void UnsubscribeSystemEvents()
    {
        if (!_systemEventsSubscribed) return;
        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            _systemEventsSubscribed = false;
        }
        catch { }
    }

    private static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _isSuspended = true;
            ResetState();
        }
        else if (e.Mode == PowerModes.Resume)
        {
            _isSuspended = false;
            ResetStateWithGrace(RESUME_GRACE_SECONDS);
        }
    }

    private static void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            _isSessionLocked = true;
            ResetState();
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            _isSessionLocked = false;
            ResetStateWithGrace(15);
        }
    }

    private static void ResetState()
    {
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastPongTicks, now);
        Interlocked.Exchange(ref _lastPingSentTicks, now);
        Interlocked.Exchange(ref _isPingPending, 0);
        _freezeReported = false;
    }

    private static void ResetStateWithGrace(int graceSeconds)
    {
        long now = Environment.TickCount64;
        Interlocked.Exchange(ref _lastPongTicks, now);
        Interlocked.Exchange(ref _lastPingSentTicks, now);
        Interlocked.Exchange(ref _isPingPending, 0);
        Interlocked.Exchange(ref _graceUntilTicks, now + (graceSeconds * 1000));
        _freezeReported = false;
    }

    private static void WatchdogLoop()
    {
        while (_isRunning && _targetDispatcher != null)
        {
            try
            {
                Thread.Sleep(2000);
                if (!_isRunning) break;

                long now = Environment.TickCount64;

                // スリープ中・セッションロック中・復帰後猶予期間中は監視を一時停止
                if (_isSuspended || _isSessionLocked || now < Interlocked.Read(ref _graceUntilTicks))
                {
                    ResetState();
                    continue;
                }

                // 前回の Ping が完了していない場合の滞留時間を測定
                if (Interlocked.CompareExchange(ref _isPingPending, 0, 0) == 1)
                {
                    long pingSent = Interlocked.Read(ref _lastPingSentTicks);
                    long pendingMs = now - pingSent;

                    if (pendingMs >= TIMEOUT_SECONDS * 1000 && !_freezeReported)
                    {
                        _freezeReported = true;
                        string errorCode = "0x80000008 (UI_THREAD_HANG_TIMEOUT)";
                        string msg = $"UIスレッドが {pendingMs / 1000.0:F0} 秒以上応答していません。オーディオサービスやシステムAPIとの待機またはデッドロックが発生している可能性があります。";

                        AppLogger.Error("FreezeWatchdog", $"{msg} | {errorCode}");

                        var sb = new StringBuilder();
                        sb.AppendLine($"[フリーズ検知情報]");
                        sb.AppendLine($"無応答時間: {pendingMs / 1000.0:F1} 秒");
                        try
                        {
                            var proc = Process.GetCurrentProcess();
                            sb.AppendLine($"プロセスID: {proc.Id}");
                            sb.AppendLine($"ワーキングセット: {proc.WorkingSet64 / 1024 / 1024} MB");
                            sb.AppendLine($"スレッド数: {proc.Threads.Count}");
                        }
                        catch { }
                        sb.AppendLine();
                        sb.AppendLine("※アプリのタスクを安全に再同期中、または再起動を推奨します。");

                        ErrorDialogService.ShowError(
                            "SanmiToys 応答停止（フリーズ）の検知",
                            msg,
                            errorCode,
                            sb.ToString()
                        );
                    }
                }
                else
                {
                    // 新たなハートビート Ping を送信
                    Interlocked.Exchange(ref _lastPingSentTicks, now);
                    Interlocked.Exchange(ref _isPingPending, 1);

                    try
                    {
                        _targetDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                        {
                            long pongNow = Environment.TickCount64;
                            Interlocked.Exchange(ref _lastPongTicks, pongNow);
                            Interlocked.Exchange(ref _isPingPending, 0);
                            _freezeReported = false;
                        }));
                    }
                    catch
                    {
                        Interlocked.Exchange(ref _isPingPending, 0);
                    }
                }
            }
            catch { }
        }
    }
}
