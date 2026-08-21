# タスク管理: SV不具合修正＆ドネーションURL設定

- [x] 1. `LocalizationService.cs` にマイクミュートHUD用キー（`SwiftVolume_Hud_MicMuted`, `SwiftVolume_Hud_MicUnmuted`）を多言語追加 <!-- id: 0 -->
- [x] 2. `VolumeHudWindow.xaml` / `VolumeHudWindow.xaml.cs` にマイクミュート専用HUD表示メソッド（`ShowMicMute`）を追加 <!-- id: 1 -->
- [x] 3. `SwiftVolumeTrayManager.cs` の `UpdateIcons` を即時引数受け取り＆確実に描画更新するよう修正 <!-- id: 2 -->
- [x] 4. `SwiftVolumeModule.cs` のホットキーハンドラでスピーカーミュート同期＆マイクミュートHUD表示を呼び出し <!-- id: 3 -->
- [x] 5. `GeneralSettingsPage.xaml.cs` と `README.md` の OFUSE / Buy Me a Coffee URL を更新 <!-- id: 4 -->
- [x] 6. ビルド検証＆インストーラー再ビルド <!-- id: 5 -->
