using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Threading;

namespace SanmiToys.Core.Services;

public static class FreezeWatchdogService
{
    private static Thread? _watchdogThread;
    private static Dispatcher? _targetDispatcher;
    private static volatile bool _isRunning;
    private static long _lastHeartbeatTicks;
    private static bool _freezeReported = false;
    private const int TIMEOUT_SECONDS = 10;

    public static void Start(Dispatcher dispatcher)
    {
        if (_isRunning) return;
        _targetDispatcher = dispatcher;
        _isRunning = true;
        _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
        _freezeReported = false;

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
    }

    private static void WatchdogLoop()
    {
        while (_isRunning && _targetDispatcher != null)
        {
            try
            {
                Thread.Sleep(2000);
                if (!_isRunning) break;

                // UIスレッドにハートビートを送信
                var dispatched = false;
                try
                {
                    _targetDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
                    {
                        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
                        _freezeReported = false;
                    }));
                    dispatched = true;
                }
                catch { }

                if (!dispatched) continue;

                // 経過時間の測定
                long lastTicks = Interlocked.Read(ref _lastHeartbeatTicks);
                var elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastTicks);

                if (elapsed.TotalSeconds >= TIMEOUT_SECONDS && !_freezeReported)
                {
                    _freezeReported = true;
                    string errorCode = "0x80000008 (UI_THREAD_HANG_TIMEOUT)";
                    string msg = $"UIスレッドが {elapsed.TotalSeconds:F0} 秒以上応答していません。オーディオサービスやシステムAPIとの待機またはデッドロックが発生している可能性があります。";

                    AppLogger.Error("FreezeWatchdog", $"{msg} | {errorCode}");

                    var sb = new StringBuilder();
                    sb.AppendLine($"[フリーズ検知情報]");
                    sb.AppendLine($"無応答時間: {elapsed.TotalSeconds:F1} 秒");
                    sb.AppendLine($"プロセスID: {Process.GetCurrentProcess().Id}");
                    sb.AppendLine($"ワーキングセット: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024} MB");
                    sb.AppendLine($"スレッド数: {Process.GetCurrentProcess().Threads.Count}");
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
            catch { }
        }
    }
}
