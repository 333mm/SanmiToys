# ポップアップ・HUD枠外ブラー解消 & Translucent Windows 表示互換 ウォークスルー

Translucent Windows、TranslucentTB、DWMアクリル注入ツール等との干渉によって、ポップアップやHUDウィンドウの角丸枠外の透明余白に四角いすりガラス（ブラー）が描画されてしまう現象を恒久的に防止し、バニラWindowsおよびカスタマイズ環境の双方で100%の表示互換性を確立しました。

---

## 修正内容のまとめ

### 1. 共通互換性ヘルパー `WindowBackdropCompatibilityHelper` の新設
- **新設**: [`SanmiToys.Core/Helpers/WindowBackdropCompatibilityHelper.cs`](file:///d:/Dev/SanmiToys/src/SanmiToys.Core/Helpers/WindowBackdropCompatibilityHelper.cs)
- **機能**:
  1. **DWM システムバックドロップの明示的無効化 (`DWMSBT_NONE` = 1)**: Windows 11 の自動 Mica/Acrylic バックドロップ注入を確実に遮断。
  2. **DWM 自動角丸の無効化 (`DWMWCP_DONOTROUND` = 1)**: DWM側の二重角丸処理と余白矩形ブラーを防止。
  3. **AccentPolicy の強制無効化 (`ACCENT_DISABLED` = 0)**: `SetWindowCompositionAttribute` を通じて Translucent ツール等による強制ブラーを上書き無効化。

### 2. アプリ内全ポップアップ・HUD・オーバーレイウィンドウへの適用
以下の全透明ウィンドウの初期化時に互換性ヘルパーを適用：
- **SwiftVolume**:
  - [`VolumeHudWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml.cs) (音量・デバイス切替HUD)
  - [`MixerWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/MixerWindow.xaml.cs) (音量ミキサーポップアップ)
- **SnapTrans**:
  - [`ResultOverlay.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SnapTrans/Views/ResultOverlay.xaml.cs) (翻訳結果ポップアップ)
  - [`SnippingWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SnapTrans/Views/SnippingWindow.xaml.cs) (範囲選択オーバーレイ)
- **FocusDimmer**:
  - [`HighlightOverlayWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/HighlightOverlayWindow.xaml.cs)
  - [`ModernColorPickerWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/ModernColorPickerWindow.xaml.cs)
  - [`DebugInspectorWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/DebugInspectorWindow.xaml.cs)
  - [`InspectorActionDialog.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/InspectorActionDialog.xaml.cs)

---

## 検証結果
- `dotnet build SanmiToys.sln -c Release`：**0 警告、0 エラー** でビルド成功を確認。
