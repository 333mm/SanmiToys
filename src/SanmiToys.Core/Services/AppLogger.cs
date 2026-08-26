using System;
using System.IO;
using System.Text;

namespace SanmiToys.Core.Services;

public static class AppLogger
{
    private static readonly object _lock = new();
    private static readonly string _logDirectory;

    static AppLogger()
    {
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _logDirectory = Path.Combine(appData, "SanmiToys", "logs");
            Directory.CreateDirectory(_logDirectory);
            CleanOldLogs();
        }
        catch
        {
            _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
        }
    }

    public static void Info(string module, string message) => Write("INFO", module, message);
    public static void Warn(string module, string message) => Write("WARN", module, message);
    public static void Error(string module, string message, Exception? ex = null)
    {
        string fullMsg = ex != null ? $"{message} | Exception: {ex}" : message;
        Write("ERROR", module, fullMsg);
    }

    private static void Write(string level, string module, string message)
    {
        try
        {
            string dateStr = DateTime.Now.ToString("yyyyMMdd");
            string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string logFile = Path.Combine(_logDirectory, $"app_{dateStr}.log");
            string logLine = $"[{timeStr}] [{level}] [{module}] {message}{Environment.NewLine}";

            lock (_lock)
            {
                File.AppendAllText(logFile, logLine, Encoding.UTF8);
            }

            System.Diagnostics.Debug.WriteLine($"[{level}] [{module}] {message}");
        }
        catch
        {
            // ロガー自体が例外を投げてアプリを止めないよう保護
        }
    }

    private static void CleanOldLogs()
    {
        try
        {
            if (!Directory.Exists(_logDirectory)) return;
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var file in Directory.GetFiles(_logDirectory, "app_*.log"))
            {
                var fi = new FileInfo(file);
                if (fi.CreationTime < cutoff)
                {
                    try { fi.Delete(); } catch { }
                }
            }
        }
        catch { }
    }
}
