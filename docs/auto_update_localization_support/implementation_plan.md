# 実装計画: 全モジュールローカライズ漏れ完全解消 & モダンUIフォント適用

## ユーザー確認事項 (User Review Required)

> [!IMPORTANT]
> **各言語におけるモダンUIフォントの採用方針**
> Windows 11 / 10 において現在最も視認性が高くモダンとされるフォントチェーンを各言語に割り当てます：
> - **日本語**: `Segoe UI Variable Text, Yu Gothic UI, Meiryo UI`
> - **英語・欧州言語 (英語/独/仏/西)**: `Segoe UI Variable Text, Segoe UI, Aptos, Inter`
> - **簡体字中国語**: `Segoe UI Variable Text, Microsoft YaHei UI` (微软雅黑)
> - **繁体字中国語**: `Segoe UI Variable Text, Microsoft JhengHei UI` (微軟正黑體)
> - **韓国語**: `Segoe UI Variable Text, Malgun Gothic` (맑은 고딕)
> 
> 言語切り替え時にアプリケーション全体のフォントファミリーが連動して切り替わります。

## 変更内容の概要

### 1. 各言語向けモダンフォント（FontFamily）の動的配信
- `LocalizationService` に `CurrentFontFamily` プロパティを追加。
- マークアップ拡張 `{loc:Font}` を新設し、各ウィンドウ・ページのルートやスタイルにバインド。

### 2. ローカライズ漏れの全数解消
以下のすべてのXAMLおよびC#コードビハインド内のハードコードテキストをローカライズキーに置き換えます：
- **Host**: `MainWindow.xaml`, `GeneralSettingsPage.xaml`, `TrayIconService.cs`
- **FluidDrag**: `FluidDragSettingsView.xaml`
- **FocusDimmer**: `FocusDimmerSettingsView.xaml`, `InspectorActionDialog.xaml`, `ModernColorPickerWindow.xaml`, `DebugInspectorWindow.xaml`
- **SnapTrans**: `SnapTransSettingsView.xaml`, `ResultOverlay.xaml` (ツールチップ・ボタン), `SnippingWindow.xaml`
- **SwiftVolume**: `SwiftVolumeSettingsView.xaml`, `MixerWindow.xaml` (ツールチップ・見出し), `VolumeHudWindow.xaml` (デバイス切り替えタイトル), `SwiftVolumeTrayManager.cs`

### 3. 多言語辞書（8言語）への全キー追加
不足している約40個のキー（ダイアログ、ツールチップ、詳細説明、ボタン、カラー名等）を日本語・英語・簡体字中・繁体字中・韓・独・仏・西の全辞書に追加。

---

## 検証手順 (Verification Plan)
1. **ビルド検証**: `dotnet build SanmiToys.sln`（エラー0・警告0）。
2. **ハードコードスキャン**: XAMLおよびCSファイルからハードコードされた日本語・英語UI文字列が一切残っていないことをPowerShellスクリプトで全数検査。
3. **言語切り替えテスト**: 各言語に切り替えた際、フォントおよび全画面（設定画面、ポップアップ、オーバーレイ、ミキサー、ダイアログ）が指定言語に翻訳されることを確認。
