# タスクリスト: SV音量ステップ削除 & STrans不要スペース排除

- [x] **SV (SwiftVolume)**: <!-- id: 0 -->
  - [x] `SwiftVolumeSettings.cs` からステップ設定・タスクバースクロール設定を削除 / 1%固定化 <!-- id: 1 -->
  - [x] `SwiftVolumeSettingsView.xaml` / `xaml.cs` からステップ設定・タスクバースクロールUIを削除 <!-- id: 2 -->
  - [x] `GlobalVolumeWheelEngine.cs` および `SwiftVolumeTrayManager.cs` でトレイアイコン上のみ 1% ずつ音量調節するように設定 <!-- id: 3 -->
  - [x] `VolumeHudWindow.xaml.cs` の初回/2回目以降の表示位置ズレ解消（決定論的サイズ計測導入） <!-- id: 4 -->
- [x] **STrans (SnapTrans)**: <!-- id: 5 -->
  - [x] `OcrService.cs` にインテリジェント単語結合（`FormatOcrResult`）およびCJKスペースクリーンアップ（`CleanOcrText`）を実装 <!-- id: 6 -->
  - [x] クリップボードコピー・翻訳テキストへの不要スペース排除の適用 <!-- id: 7 -->
- [x] ビルド検証（0 警告、0 エラー） <!-- id: 8 -->
- [x] ドキュメント更新・保存 <!-- id: 9 -->
