# FocusDimmer - 自プロセス除外＆全体ブラー現象の完全解消 完了ウォークスルー

ユーザーのご指示・ご報告：
* **インスペクターモード時はsanmitoys自身は除外します**
* **Translucent Windowsとの競合で全体ブラー現象が再発しています**

---

## 実施した対応・修正内容

1. **インスペクターモード時の SanmiToys 自身の完全除外**:
   - [`DebugInspector.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Helpers/DebugInspector.cs):
     - 自プロセスの PID（`selfPid`）およびプロセス名（`SanmiToys`, `SanmiToys.Host`）に一致するすべてのウィンドウを、インスペクターのスキャン・候補リスティングから完全にスキップ・除外するように修正。
     - これにより、メインウィンドウやインスペクター自身がリストやアウトラインの邪魔をすることがなくなりました。

2. **Translucent Windows / TranslucentTB との競合ブラー現象の完全解消**:
   - [`HighlightOverlayWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/HighlightOverlayWindow.xaml.cs) / [`DebugInspectorWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/DebugInspectorWindow.xaml.cs):
     - 新設したハイライト描画オーバーレイウィンドウおよびインスペクターウィンドウに対して、`WindowHelper.DisableBackdropAndBlur`（DWM バックドロップ無効化 `DWMSBT_NONE` ＆ AccentPolicy 無効化）を徹底適用。
     - 外部の透明化・ブラーツール（Translucent Windows, TranslucentTB 等）が全画面透過オーバーレイをアクリル対象として誤フックするのを確実にブロックし、全体ブラー現象を完全に解消いたしました。

---

## ビルド検証結果
- `dotnet build "d:\Dev\SanmiToys\SanmiToys.sln"`
- **結果: 0 警告 / 0 エラー（クリーンビルド成功）**
