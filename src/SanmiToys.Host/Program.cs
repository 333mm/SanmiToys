using System;
using Velopack;

namespace SanmiToys.Host;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack ライフサイクルフック（インストール・更新・ショートカット生成を処理）
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
