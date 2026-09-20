# タスク: SwiftVolume画面埋まり問題 & SnapTransマインクラフト等ミニポップアップ未表示問題の修正

## 課題
1. **SwiftVolume**: 再表示時などにウィンドウが画面の下（タスクバー下・画面外）に埋まってしまう問題。
2. **SnapTrans**: マインクラフトクライアント等のテキスト選択可能な場面でホットキー（修飾キー／グローバルホットキー）を押下してもミニポップアップが表示されない問題。

## 作業項目
- [x] SwiftVolume: `MixerWindow` の再表示時画面埋まり解消
  - [x] `CloseWindowSafely` での `AppSessionsPanel.Children.Clear()` 削除（前回の適正サイズを維持）
  - [x] `UpdateWindowPosition` および `AdjustPositionOnSizeChanged` での決定論的コンテンツサイズ計測・Win32 `SetWindowPos` 座標更新の導入
  - [x] 画面外クランプ処理の堅牢化
- [x] SnapTrans: マインクラフト等の非 UI Automation アプリでのミニポップアップ表示対応
  - [x] `TextSelectionEngine` にクリップボード経由の安全なフォールバック（Ctrl+C送信＋元クリップボード復元）を実装
  - [x] キャプチャホットキー押下時にもテキスト選択中ならミニポップアップを表示する連携を追加
  - [x] `SelectionMiniToolbar` の最前面表示（Topmost）の確実化
- [x] 関連するUnit Testの作成・追加
- [x] ビルド検証（Release ビルド確認、Unit Test 全件パス確認）
