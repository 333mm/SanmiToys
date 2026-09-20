# タスク: SV UI放置後画面下見切れ問題の完全解消 & OG画面最前面表示メニューの追加

## 課題
1. **SwiftVolume**: しばらく放置後に SV UI を開くと、セッション数の変動やレイアウト遅延等により画面下（タスクバー下）に見切れてしまう問題。
2. **OmniGlance**: OG 右クリックメニューに「画面最前面に表示」オプションを追加し、デフォルトを ON にする。

## 作業項目
- [x] SwiftVolume: 画面下見切れの完全解消
  - [x] `WM_WINDOWPOSCHANGING` フックを実装し、ウィンドウサイズ変更時にOSレベルで `y + cy > workBottom` を検知して自動的に底面（`workBottom - cy - margin`）に吸着
  - [x] `SizeChanged` ハンドラで `Dispatcher.BeginInvoke(DispatcherPriority.Render)` による遅延位置補正を適用（WPF内部リサイズトランザクション完了後の確実な補正）
  - [x] `MixerWindow.xaml` から競合の原因となる `Height="460"` を削除
  - [x] マルチモニター対応: カーソル位置モニターの正確なワークエリア高さに基づいて `ScrollViewer.MaxHeight` および `Window.MaxHeight` を設定
- [x] OmniGlance: 画面最前面に表示オプションの追加
  - [x] `OmniGlanceSettings.cs` に `AlwaysOnTop` プロパティを追加（デフォルト `true`）
  - [x] `OmniIslandWindow.xaml.cs` の右クリックメニューに「画面最前面に表示」項目を追加（チェック状態・クリック連動）
  - [x] `ApplySettings` / `ApplyWindowStyle` で `Topmost` および Win32 `SetWindowPos(HWND_TOPMOST / HWND_NOTOPMOST)` を反映
  - [x] `LocalizationService.cs` に全8言語の翻訳キー `OmniGlance_Menu_AlwaysOnTop` を追加
  - [x] 設定画面 `OmniGlanceSettingsView` にも連動トグルを追加
- [x] ユニットテストの作成・追加
- [x] ビルド検証（Release ビルド確認、Unit Test 全件パス確認）
