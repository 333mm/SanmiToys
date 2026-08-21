# タスクリスト: ポップアップ・HUDウィンドウの枠外ブラー解消 & Translucent Windows 表示互換

- [x] `WindowBackdropCompatibilityHelper` の作成（DWMバックドロップ・角丸・AccentPolicyの無効化処理の集約） <!-- id: 0 -->
- [x] 各モジュールのポップアップ・HUD・オーバーレイウィンドウへの互換性ヘルパー適用 <!-- id: 1 -->
  - [x] SwiftVolume: `VolumeHudWindow.xaml.cs`, `MixerWindow.xaml.cs`
  - [x] SnapTrans: `ResultOverlay.xaml.cs`, `SnippingWindow.xaml.cs`
  - [x] FocusDimmer: `HighlightOverlayWindow.xaml.cs`, `ModernColorPickerWindow.xaml.cs`, `DebugInspectorWindow.xaml.cs`, `InspectorActionDialog.xaml.cs`
- [x] ビルド検証（0 警告、0 エラー） <!-- id: 2 -->
- [x] ドキュメント更新・保存 <!-- id: 3 -->
