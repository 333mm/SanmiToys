using System;
using Velopack;

namespace SanmiToys.Host;

public static class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Windows 10 (1703+) / Windows 11 の Per-Monitor V2 高DPIコンテキストを最優先で確立
            SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }

        // Velopack ライフサイクルフック（インストール・更新・ショートカット生成を処理）
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
