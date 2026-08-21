# ポップアップ・HUDウィンドウの枠外ブラー解消 & Translucent Windows 表示互換 実装計画

Translucent Windows や TranslucentTB、DWMアクリル注入ツールなどが動作している環境において、WPFの透明ウィンドウ（`AllowsTransparency="True"`）の外周余白に四角いすりガラス（ブラー）が誤って描画されてしまう現象を恒久的に防止し、バニラWindowsおよび拡張環境の双方で100%の表示互換性を維持します。

---

## 課題と原因分析

- **課題**: ポップアップ（音量ミキサー等）やHUDウィンドウ（音量HUD、オーバーレイ等）の角丸枠外の背景に、不要な四角形のブラーがかかってしまう。
- **原因**: 
  - Translucent Windows や DWM アクリル注入ツールがトップレベル HWND を検出し、ウィンドウの四角形クライアント領域全体に対して `SetWindowCompositionAttribute`（アクリル/ブラー）や Windows 11 のシステムバックドロップ（`DWMWA_SYSTEMBACKDROP_TYPE`）を強制注入してしまう。
  - WPF 側は角丸の外側を透明ピクセル（ARGB=0,0,0,0）として描画しているが、DWM側が HWND 矩形全体にブラーをかけるため、透明なはずの余白部分が四角く曇って表示される。

---

## 提案する変更内容

### 1. 共通互換性ヘルパー `WindowBackdropCompatibilityHelper` の作成
`SanmiToys.Core.Helpers` に新設し、透明ポップアップ・HUDウィンドウの初期化（`SourceInitialized`）時に以下を自動適用：
- **DWM システムバックドロップの明示的無効化 (`DWMSBT_NONE` = 1)**: Windows 11 の自動アクリル/Mica適用を遮断。
- **DWM 自動角丸の無効化 (`DWMWCP_DONOTROUND` = 1)**: DWM側の二重角丸ブラーを抑止。
- **AccentPolicy の明示的無効化 (`ACCENT_DISABLED` = 0)**: `SetWindowCompositionAttribute` を通じて Translucent ツール等による強制ブラーを上書き無効化。

### 2. 対象ウィンドウへの適用
以下の全透明ウィンドウに `WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this)` を適用：
- **SwiftVolume**:
  - `VolumeHudWindow.xaml.cs`
  - `MixerWindow.xaml.cs`
- **SnapTrans**:
  - `ResultOverlay.xaml.cs`
  - `SnippingWindow.xaml.cs`
- **FocusDimmer**:
  - `HighlightOverlayWindow.xaml.cs`
  - `ModernColorPickerWindow.xaml.cs`
  - `DebugInspectorWindow.xaml.cs`
  - `InspectorActionDialog.xaml.cs`

---

## 変更対象ファイル

- [NEW] [`SanmiToys.Core/Helpers/WindowBackdropCompatibilityHelper.cs`](file:///d:/Dev/SanmiToys/src/SanmiToys.Core/Helpers/WindowBackdropCompatibilityHelper.cs)
- [MODIFY] [`SanmiToys.Core/NativeMethods.cs`](file:///d:/Dev/SanmiToys/src/SanmiToys.Core/NativeMethods.cs)
- [MODIFY] [`VolumeHudWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml.cs)
- [MODIFY] [`MixerWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/MixerWindow.xaml.cs)
- [MODIFY] [`ResultOverlay.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SnapTrans/Views/ResultOverlay.xaml.cs)
- [MODIFY] [`SnippingWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SnapTrans/Views/SnippingWindow.xaml.cs)
- [MODIFY] [`HighlightOverlayWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/HighlightOverlayWindow.xaml.cs)
- [MODIFY] [`ModernColorPickerWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/ModernColorPickerWindow.xaml.cs)
- [MODIFY] [`DebugInspectorWindow.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/DebugInspectorWindow.xaml.cs)
- [MODIFY] [`InspectorActionDialog.xaml.cs`](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.FocusDimmer/Views/InspectorActionDialog.xaml.cs)

---

## 検証計画
- `dotnet build SanmiToys.sln -c Release` を実行し、警告 0・エラー 0 を確認。
