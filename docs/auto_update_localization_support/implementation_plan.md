# SwiftVolume 不具合修正＆ドネーションURL更新計画

## 概要
1. **SwiftVolume スピーカーミュート解除時のトレイアイコン同期不具合**を修正
2. **マイクミュートショートカット押下時に HUD が表示されない問題**を修正（マイクミュートHUD表示機能を追加）
3. **開発者支援（OFUSE / Buy Me a Coffee）のURL**をユーザー固有のURLに更新

---

## 原因分析と修正方針

### 1. スピーカーミュート解除時のトレイアイコン同期
- **原因**: `SwiftVolumeModule` の `OnVolumeChanged(float, bool)` でトレイアイコンを更新する際、`SwiftVolumeTrayManager.UpdateIcons()` が渡された引数（最新状態）を無視して再度 `AudioDeviceHelper` をポーリングしており、キャッシュ値やタイミング差でミュート解除アイコンの更新がスキップされていた。
- **対策**: `UpdateIcons(float? vol = null, bool? isMuted = null)` に拡張し、明示的に渡された音量・ミュート状態を即時反映しアイコンキャッシュも確実に再描画する。

### 2. マイクミュート時の HUD 表示
- **原因**: `HOTKEY_ID_MIC_MUTE` 押下時の処理で `_hudWindow` の表示メソッドが呼ばれておらず、`VolumeHudWindow` にもマイクミュート専用のHUD表示モードがなかった。
- **対策**:
  - `VolumeHudWindow` に `ShowMicMute(bool isMuted, ...)` メソッドを追加（マイクアイコン + 「マイク ミュート」/「マイク ミュート解除」バッジ表示）。
  - `SwiftVolumeModule.cs` のホットキーハンドラから `_hudWindow.ShowMicMute(...)` を呼び出す。

### 3. ドネーションURLの更新
- `GeneralSettingsPage.xaml.cs` および `README.md` の URL を以下に更新：
  - OFUSE: `https://ofuse.me/d3a3316d`
  - Buy Me a Coffee: `https://buymeacoffee.com/sanmi`

---

## 提案する変更

### 1. [SwiftVolume] トレイマネージャー＆モジュール
#### [MODIFY] [SwiftVolumeTrayManager.cs](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Core/SwiftVolumeTrayManager.cs)
- `UpdateIcons(float? explicitVol = null, bool? explicitMuted = null)` を実装し即時更新を保証

#### [MODIFY] [SwiftVolumeModule.cs](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/SwiftVolumeModule.cs)
- `HOTKEY_ID_MUTE` 時に `_trayManager.UpdateIcons(vol, muted)` を呼び出し
- `HOTKEY_ID_MIC_MUTE` 時に `_hudWindow.ShowMicMute(isMuted, ...)` を呼び出し

### 2. [SwiftVolume] HUD 表示ウィンドウ
#### [MODIFY] [VolumeHudWindow.xaml](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml)
#### [MODIFY] [VolumeHudWindow.xaml.cs](file:///d:/Dev/SanmiToys/src/Modules/SanmiToys.Modules.SwiftVolume/Views/VolumeHudWindow.xaml.cs)
- マイクミュート／ミュート解除時の専用 HUD 表示 UI（アイコン + ステータス文字）を追加

### 3. [SanmiToys.Core] 多言語辞書
#### [MODIFY] [LocalizationService.cs](file:///d:/Dev/SanmiToys/src/SanmiToys.Core/Services/LocalizationService.cs)
- `SwiftVolume_Hud_MicMuted` ("マイク ミュート" / "Microphone Muted")
- `SwiftVolume_Hud_MicUnmuted` ("マイク ミュート解除" / "Microphone Unmuted")

### 4. [SanmiToys.Host] ドネーション設定
#### [MODIFY] [GeneralSettingsPage.xaml.cs](file:///d:/Dev/SanmiToys/src/SanmiToys.Host/Views/GeneralSettingsPage.xaml.cs)
- `DONATE_OFUSE_URL` と `DONATE_BUYMEACOFFEE_URL` を指定の URL に更新
#### [MODIFY] [README.md](file:///d:/Dev/SanmiToys/README.md)
- 支援セクションの URL を更新

---

## 検証計画
1. `dotnet build SanmiToys.sln` でエラー・警告ゼロを確認
2. インストーラーを再ビルド
